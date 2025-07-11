﻿// Copyright (c) Microsoft. All rights reserved.
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Agents.Extensions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.FunctionCalling;
using OpenAI.Assistants;

namespace Microsoft.SemanticKernel.Agents.OpenAI.Internal;

/// <summary>
/// Actions associated with an OpenAI Assistant thread.
/// </summary>
internal static class AssistantThreadActions
{
    private static readonly HashSet<RunStatus> s_pollingStatuses =
    [
        RunStatus.Queued,
        RunStatus.InProgress,
        RunStatus.Cancelling,
    ];

    /// <summary>
    /// Create a message in the specified thread.
    /// </summary>
    /// <param name="client">The assistant client</param>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="message">The message to add</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <throws><see cref="KernelException"/> if a system message is present, without taking any other action</throws>
    public static async Task CreateMessageAsync(AssistantClient client, string threadId, ChatMessageContent message, CancellationToken cancellationToken)
    {
        if (message.Items.Any(i => i is FunctionCallContent))
        {
            return;
        }

        MessageCreationOptions options = AssistantMessageFactory.CreateOptions(message);
        IEnumerable<MessageContent> content = AssistantMessageFactory.GetMessageContents(message);
        if (!content.Any())
        {
            return;
        }

        await client.CreateMessageAsync(
            threadId,
            message.Role == AuthorRole.User ? MessageRole.User : MessageRole.Assistant,
            content,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the thread messages.
    /// </summary>
    /// <param name="client">The assistant client</param>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="messageOrder">The order to return messages in.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public static async IAsyncEnumerable<ChatMessageContent> GetMessagesAsync(AssistantClient client, string threadId, MessageCollectionOrder? messageOrder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Dictionary<string, string?> agentNames = []; // Cache agent names by their identifier

        await foreach (ThreadMessage message in client.GetMessagesAsync(threadId, new() { Order = messageOrder ?? MessageCollectionOrder.Descending }, cancellationToken).ConfigureAwait(false))
        {
            string? assistantName = null;
            if (!string.IsNullOrWhiteSpace(message.AssistantId) &&
                !agentNames.TryGetValue(message.AssistantId, out assistantName))
            {
                Assistant assistant = await client.GetAssistantAsync(message.AssistantId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(assistant.Name))
                {
                    agentNames.Add(assistant.Id, assistant.Name);
                }
            }

            assistantName ??= message.AssistantId;

            ChatMessageContent content = GenerateMessageContent(assistantName, message);

            if (content.Items.Count > 0)
            {
                yield return content;
            }
        }
    }

    /// <summary>
    /// Invoke the assistant on the specified thread.
    /// In the enumeration returned by this method, a message is considered visible if it is intended to be displayed to the user.
    /// Example of a non-visible message is function-content for functions that are automatically executed.
    /// </summary>
    /// <param name="agent">The assistant agent to interact with the thread.</param>
    /// <param name="client">The assistant client</param>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="invocationOptions">Options to utilize for the invocation</param>
    /// <param name="providersAdditionalInstructions">Additional instructions from <see cref="AIContextProvider"/> instances to pass to the invoke method.</param>
    /// <param name="logger">The logger to utilize (might be agent or channel scoped)</param>
    /// <param name="kernel">The <see cref="Kernel"/> plugins and other state.</param>
    /// <param name="arguments">Optional arguments to pass to the agents's invocation, including any <see cref="PromptExecutionSettings"/>.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    public static async IAsyncEnumerable<(bool IsVisible, ChatMessageContent Message)> InvokeAsync(
        OpenAIAssistantAgent agent,
        AssistantClient client,
        string threadId,
        RunCreationOptions? invocationOptions,
        string? providersAdditionalInstructions,
        ILogger logger,
        Kernel kernel,
        KernelArguments? arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogOpenAIAssistantCreatingRun(nameof(InvokeAsync), threadId);

        List<ToolDefinition> tools = new(agent.Definition.Tools);

        // Add unique functions from the Kernel which are not already present in the agent's tools
        var functionToolNames = new HashSet<string>(tools.OfType<FunctionToolDefinition>().Select(t => t.FunctionName));
        var functionTools = kernel.Plugins
            .SelectMany(kp => kp.Select(kf => kf.ToToolDefinition(kp.Name)))
            .Where(tool => !functionToolNames.Contains(tool.FunctionName));
        tools.AddRange(functionTools);

        string? instructions = await agent.GetInstructionsAsync(kernel, arguments, cancellationToken).ConfigureAwait(false);

        RunCreationOptions options = AssistantRunOptionsFactory.GenerateOptions(agent.RunOptions, instructions, invocationOptions, providersAdditionalInstructions);

        options.ToolsOverride.AddRange(tools);

        ThreadRun run = await client.CreateRunAsync(threadId, agent.Id, options, cancellationToken).ConfigureAwait(false);

        logger.LogOpenAIAssistantCreatedRun(nameof(InvokeAsync), run.Id, threadId);

        FunctionCallsProcessor functionProcessor = new(logger);
        // This matches current behavior.  Will be configurable upon integrating with `FunctionChoice` (#6795/#5200)
        FunctionChoiceBehaviorOptions functionOptions = new() { AllowConcurrentInvocation = true, AllowParallelCalls = true };

        // Evaluate status and process steps and messages, as encountered.
        HashSet<string> processedStepIds = [];
        Dictionary<string, FunctionResultContent> functionSteps = [];
        do
        {
            // Check for cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // Poll run and steps until actionable
            await PollRunStatusAsync().ConfigureAwait(false);

            // Is in terminal state?
            if (run.Status.IsTerminal && run.Status != RunStatus.Completed)
            {
                throw new KernelException($"Agent Failure - Run terminated: {run.Status} [{run.Id}]: {run.LastError?.Message ?? "Unknown"}");
            }

            RunStep[] steps = await client.GetRunStepsAsync(run.ThreadId, run.Id, cancellationToken: cancellationToken).ToArrayAsync(cancellationToken).ConfigureAwait(false);

            // Is tool action required?
            if (run.Status == RunStatus.RequiresAction)
            {
                logger.LogOpenAIAssistantProcessingRunSteps(nameof(InvokeAsync), run.Id, threadId);

                // Execute functions in parallel and post results at once.
                FunctionCallContent[] functionCalls = steps.SelectMany(step => ParseFunctionStep(agent, step)).ToArray();
                if (functionCalls.Length > 0)
                {
                    // Emit function-call content
                    ChatMessageContent functionCallMessage = GenerateFunctionCallContent(agent.GetName(), functionCalls);
                    yield return (IsVisible: false, Message: functionCallMessage);

                    // Invoke functions for each tool-step
                    FunctionResultContent[] functionResults =
                        await functionProcessor.InvokeFunctionCallsAsync(
                            functionCallMessage,
                            (_) => true,
                            functionOptions,
                            kernel,
                            isStreaming: false,
                            cancellationToken).ToArrayAsync(cancellationToken).ConfigureAwait(false);

                    // Capture function-call for message processing
                    foreach (FunctionResultContent functionCall in functionResults)
                    {
                        functionSteps.Add(functionCall.CallId!, functionCall);
                    }

                    // Process tool output
                    ToolOutput[] toolOutputs = GenerateToolOutputs(functionResults);

                    await client.SubmitToolOutputsToRunAsync(threadId, run.Id, toolOutputs, cancellationToken).ConfigureAwait(false);
                }

                logger.LogOpenAIAssistantProcessedRunSteps(nameof(InvokeAsync), functionCalls.Length, run.Id, threadId);
            }

            // Enumerate completed messages
            logger.LogOpenAIAssistantProcessingRunMessages(nameof(InvokeAsync), run.Id, threadId);

            IEnumerable<RunStep> completedStepsToProcess =
                steps
                    .Where(s => s.CompletedAt.HasValue && !processedStepIds.Contains(s.Id))
                    .OrderBy(s => s.CreatedAt);

            int messageCount = 0;
            foreach (RunStep completedStep in completedStepsToProcess)
            {
                if (completedStep.Kind == RunStepKind.ToolCall)
                {
                    foreach (RunStepToolCall toolCall in completedStep.Details.ToolCalls)
                    {
                        bool isVisible = false;
                        ChatMessageContent? content = null;

                        // Process code-interpreter content
                        if (toolCall.Kind == RunStepToolCallKind.CodeInterpreter)
                        {
                            content = GenerateCodeInterpreterContent(agent.GetName(), toolCall.CodeInterpreterInput, completedStep);
                            isVisible = true;
                        }
                        // Process function result content
                        else if (toolCall.Kind == RunStepToolCallKind.Function)
                        {
                            FunctionResultContent functionStep = functionSteps[toolCall.Id]; // Function step always captured on invocation
                            content = GenerateFunctionResultContent(agent.GetName(), [functionStep], completedStep);
                        }

                        if (content is not null)
                        {
                            ++messageCount;

                            yield return (isVisible, Message: content);
                        }
                    }
                }
                else if (completedStep.Kind == RunStepKind.CreatedMessage)
                {
                    // Retrieve the message
                    ThreadMessage? message = await RetrieveMessageAsync(client, threadId, completedStep.Details.CreatedMessageId, agent.PollingOptions.MessageSynchronizationDelay, cancellationToken).ConfigureAwait(false);

                    if (message is not null)
                    {
                        ChatMessageContent content = GenerateMessageContent(agent.GetName(), message, completedStep);

                        if (content.Items.Count > 0)
                        {
                            ++messageCount;

                            yield return (IsVisible: true, Message: content);
                        }
                    }
                }

                processedStepIds.Add(completedStep.Id);
            }

            logger.LogOpenAIAssistantProcessedRunMessages(nameof(InvokeAsync), messageCount, run.Id, threadId);
        }
        while (RunStatus.Completed != run.Status);

        logger.LogOpenAIAssistantCompletedRun(nameof(InvokeAsync), run.Id, threadId);

        // Local function to assist in run polling (participates in method closure).
        async Task PollRunStatusAsync()
        {
            logger.LogOpenAIAssistantPollingRunStatus(nameof(PollRunStatusAsync), run.Id, threadId);

            int count = 0;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (count > 0)
                {
                    // Reduce polling frequency after a couple attempts
                    await Task.Delay(agent.PollingOptions.GetPollingInterval(count), cancellationToken).ConfigureAwait(false);
                }

                ++count;

                try
                {
                    run = await client.GetRunAsync(threadId, run.Id, cancellationToken).ConfigureAwait(false);
                }
                // The presence of a `Status` code means the server responded with error...always fail in that case
                catch (ClientResultException clientException) when (clientException.Status <= 0)
                {
                    // Check maximum retry count
                    if (count >= agent.PollingOptions.MaximumRetryCount)
                    {
                        throw;
                    }

                    // Retry for potential transient failure
                    continue;
                }
                catch (AggregateException aggregateException) when (aggregateException.InnerException is ClientResultException innerClientException)
                {
                    // The presence of a `Status` code means the server responded with error
                    if (innerClientException.Status > 0)
                    {
                        throw;
                    }

                    // Check maximum retry count
                    if (count >= agent.PollingOptions.MaximumRetryCount)
                    {
                        throw;
                    }

                    // Retry for potential transient failure
                    continue;
                }
            }
            while (s_pollingStatuses.Contains(run.Status));

            logger.LogOpenAIAssistantPolledRunStatus(nameof(PollRunStatusAsync), run.Status, run.Id, threadId);
        }
    }

    /// <summary>
    /// Invoke the assistant on the specified thread using streaming.
    /// </summary>
    /// <param name="agent">The assistant agent to interact with the thread.</param>
    /// <param name="client">The assistant client</param>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="messages">The receiver for the completed messages generated</param>
    /// <param name="invocationOptions">Options to utilize for the invocation</param>
    /// <param name="providersAdditionalInstructions">Additional instructions from <see cref="AIContextProvider"/> instances to pass to the invoke method.</param>
    /// <param name="logger">The logger to utilize (might be agent or channel scoped)</param>
    /// <param name="kernel">The <see cref="Kernel"/> plugins and other state.</param>
    /// <param name="arguments">Optional arguments to pass to the agents's invocation, including any <see cref="PromptExecutionSettings"/>.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>Asynchronous enumeration of messages.</returns>
    /// <remarks>
    /// The `arguments` parameter is not currently used by the agent, but is provided for future extensibility.
    /// </remarks>
    [ExcludeFromCodeCoverage]
    public static async IAsyncEnumerable<StreamingChatMessageContent> InvokeStreamingAsync(
        OpenAIAssistantAgent agent,
        AssistantClient client,
        string threadId,
        IList<ChatMessageContent>? messages,
        RunCreationOptions? invocationOptions,
        string? providersAdditionalInstructions,
        ILogger logger,
        Kernel kernel,
        KernelArguments? arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogOpenAIAssistantCreatingRun(nameof(InvokeAsync), threadId);

        ToolDefinition[]? tools = [.. agent.Definition.Tools, .. kernel.Plugins.SelectMany(p => p.Select(f => f.ToToolDefinition(p.Name)))];

        string? instructions = await agent.GetInstructionsAsync(kernel, arguments, cancellationToken).ConfigureAwait(false);

        RunCreationOptions options = AssistantRunOptionsFactory.GenerateOptions(agent.RunOptions, instructions, invocationOptions, providersAdditionalInstructions);

        options.ToolsOverride.AddRange(tools);

        // Evaluate status and process steps and messages, as encountered.
        HashSet<string> processedStepIds = [];
        Dictionary<string, FunctionResultContent[]> stepFunctionResults = [];
        List<RunStep> stepsToProcess = [];
        ThreadRun? run = null;

        FunctionCallsProcessor functionProcessor = new(logger);
        // This matches current behavior.  Will be configurable upon integrating with `FunctionChoice` (#6795/#5200)
        FunctionChoiceBehaviorOptions functionOptions = new() { AllowConcurrentInvocation = true, AllowParallelCalls = true };

        IAsyncEnumerable<StreamingUpdate> asyncUpdates = client.CreateRunStreamingAsync(threadId, agent.Id, options, cancellationToken);
        do
        {
            // Check for cancellation
            cancellationToken.ThrowIfCancellationRequested();

            stepsToProcess.Clear();

            await foreach (StreamingUpdate update in asyncUpdates.ConfigureAwait(false))
            {
                if (update is RunUpdate runUpdate)
                {
                    run = runUpdate.Value;

                    switch (runUpdate.UpdateKind)
                    {
                        case StreamingUpdateReason.RunCreated:
                            logger.LogOpenAIAssistantCreatedRun(nameof(InvokeAsync), run.Id, threadId);
                            break;
                    }
                }
                else if (update is MessageContentUpdate contentUpdate)
                {
                    switch (contentUpdate.UpdateKind)
                    {
                        case StreamingUpdateReason.MessageUpdated:
                            yield return GenerateStreamingMessageContent(agent.GetName(), run!, contentUpdate, logger);
                            break;
                    }
                }
                else if (update is RunStepDetailsUpdate detailsUpdate)
                {
                    StreamingChatMessageContent? toolContent = GenerateStreamingCodeInterpreterContent(agent.GetName(), detailsUpdate);
                    if (toolContent != null)
                    {
                        yield return toolContent;
                    }
                    else if (detailsUpdate.FunctionName != null || detailsUpdate.FunctionArguments != null)
                    {
                        yield return
                            new StreamingChatMessageContent(AuthorRole.Assistant, null)
                            {
                                AuthorName = agent.Name,
                                Items = [new StreamingFunctionCallUpdateContent(detailsUpdate.ToolCallId, detailsUpdate.FunctionName, detailsUpdate.FunctionArguments, detailsUpdate.ToolCallIndex ?? 0)],
                                InnerContent = detailsUpdate,
                            };
                    }
                }
                else if (update is RunStepUpdate stepUpdate)
                {
                    switch (stepUpdate.UpdateKind)
                    {
                        case StreamingUpdateReason.RunStepCompleted:
                            stepsToProcess.Add(stepUpdate.Value);
                            break;
                        default:
                            break;
                    }
                }
            }

            if (run == null)
            {
                throw new KernelException($"Agent Failure - Run not created for thread: ${threadId}");
            }

            // Is in terminal state?
            if (run.Status.IsTerminal && run.Status != RunStatus.Completed)
            {
                throw new KernelException($"Agent Failure - Run terminated: {run.Status} [{run.Id}]: {run.LastError?.Message ?? "Unknown"}");
            }

            if (run.Status == RunStatus.RequiresAction)
            {
                RunStep[] activeSteps =
                    await client.GetRunStepsAsync(run.ThreadId, run.Id, cancellationToken: cancellationToken)
                    .Where(step => step.Status == RunStepStatus.InProgress)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);

                // Capture map between the tool call and its associated step
                Dictionary<string, string> toolMap = [];
                foreach (RunStep step in activeSteps)
                {
                    foreach (RunStepToolCall stepDetails in step.Details.ToolCalls)
                    {
                        toolMap[stepDetails.Id] = step.Id;
                    }
                }

                // Execute functions in parallel and post results at once.
                FunctionCallContent[] functionCalls = activeSteps.SelectMany(step => ParseFunctionStep(agent, step)).ToArray();
                if (functionCalls.Length > 0)
                {
                    // Emit function-call content
                    ChatMessageContent functionCallMessage = GenerateFunctionCallContent(agent.GetName(), functionCalls);
                    messages?.Add(functionCallMessage);

                    FunctionResultContent[] functionResults =
                        await functionProcessor.InvokeFunctionCallsAsync(
                            functionCallMessage,
                            (_) => true,
                            functionOptions,
                            kernel,
                            isStreaming: true,
                            cancellationToken).ToArrayAsync(cancellationToken).ConfigureAwait(false);

                    // Process tool output
                    ToolOutput[] toolOutputs = GenerateToolOutputs(functionResults);
                    asyncUpdates = client.SubmitToolOutputsToRunStreamingAsync(run.ThreadId, run.Id, toolOutputs, cancellationToken);

                    foreach (RunStep step in activeSteps)
                    {
                        stepFunctionResults.Add(step.Id, functionResults.Where(result => step.Id == toolMap[result.CallId!]).ToArray());
                    }
                }
            }

            if (stepsToProcess.Count > 0)
            {
                logger.LogOpenAIAssistantProcessingRunMessages(nameof(InvokeAsync), run!.Id, threadId);

                foreach (RunStep step in stepsToProcess)
                {
                    if (!string.IsNullOrEmpty(step.Details.CreatedMessageId))
                    {
                        ThreadMessage? message =
                            await RetrieveMessageAsync(
                                client,
                                threadId,
                                step.Details.CreatedMessageId,
                                agent.PollingOptions.MessageSynchronizationDelay,
                                cancellationToken).ConfigureAwait(false);

                        if (message != null)
                        {
                            ChatMessageContent content = GenerateMessageContent(agent.GetName(), message, step);
                            messages?.Add(content);
                        }
                    }
                    else
                    {
                        foreach (RunStepToolCall toolCall in step.Details.ToolCalls)
                        {
                            if (toolCall.Kind == RunStepToolCallKind.Function)
                            {
                                messages?.Add(GenerateFunctionResultContent(agent.GetName(), stepFunctionResults[step.Id], step));
                                stepFunctionResults.Remove(step.Id);
                                break;
                            }

                            if (toolCall.Kind == RunStepToolCallKind.CodeInterpreter)
                            {
                                messages?.Add(GenerateCodeInterpreterContent(agent.GetName(), toolCall.CodeInterpreterInput, step));
                            }
                        }
                    }
                }

                logger.LogOpenAIAssistantProcessedRunMessages(nameof(InvokeAsync), stepsToProcess.Count, run!.Id, threadId);
            }
        }
        while (run?.Status != RunStatus.Completed);

        logger.LogOpenAIAssistantCompletedRun(nameof(InvokeAsync), run?.Id ?? "Failed", threadId);
    }

    private static ChatMessageContent GenerateMessageContent(string? assistantName, ThreadMessage message, RunStep? completedStep = null, ILogger? logger = null)
    {
        AuthorRole role = new(message.Role.ToString());

        Dictionary<string, object?>? metadata =
            new()
            {
                { nameof(ThreadMessage.CreatedAt), message.CreatedAt },
                { nameof(ThreadMessage.AssistantId), message.AssistantId },
                { nameof(ThreadMessage.ThreadId), message.ThreadId },
                { nameof(ThreadMessage.RunId), message.RunId },
                { nameof(MessageContentUpdate.MessageId), message.Id },
            };

        if (completedStep != null)
        {
            metadata[nameof(RunStepDetailsUpdate.StepId)] = completedStep.Id;
            metadata[nameof(RunStep.Usage)] = completedStep.Usage;
        }

        ChatMessageContent content =
            new(role, content: null)
            {
                AuthorName = assistantName,
                InnerContent = message,
                Metadata = metadata,
            };

        foreach (MessageContent itemContent in message.Content)
        {
            // Process text content
            if (!string.IsNullOrEmpty(itemContent.Text))
            {
                content.Items.Add(new TextContent(itemContent.Text));

                foreach (TextAnnotation annotation in itemContent.TextAnnotations)
                {
                    AnnotationContent? annotationItem = GenerateAnnotationContent(annotation);
                    if (annotationItem is not null)
                    {
                        content.Items.Add(annotationItem);
                    }
                    else
                    {
                        logger?.LogOpenAIAssistantUnknownAnnotation(nameof(GenerateMessageContent), message.RunId, message.ThreadId, annotation.GetType());
                    }
                }
            }
            // Process image content
            else if (itemContent.ImageFileId != null)
            {
                content.Items.Add(new FileReferenceContent(itemContent.ImageFileId));
            }
        }

        return content;
    }

    [ExcludeFromCodeCoverage]
    private static StreamingChatMessageContent GenerateStreamingMessageContent(string? assistantName, ThreadRun run, MessageContentUpdate update, ILogger? logger)
    {
        StreamingChatMessageContent content =
            new(AuthorRole.Assistant, content: null)
            {
                AuthorName = assistantName,
                InnerContent = update,
            };

        // Process text content
        if (!string.IsNullOrEmpty(update.Text))
        {
            content.Items.Add(new StreamingTextContent(update.Text));
        }
        // Process image content
        else if (update.ImageFileId != null)
        {
            content.Items.Add(new StreamingFileReferenceContent(update.ImageFileId));
        }
        // Process annotations
        else if (update.TextAnnotation != null)
        {
            StreamingAnnotationContent? annotationItem = GenerateStreamingAnnotationContent(update.TextAnnotation);
            if (annotationItem is not null)
            {
                content.Items.Add(annotationItem);
            }
            else
            {
                logger?.LogOpenAIAssistantUnknownAnnotation(nameof(GenerateMessageContent), run.Id, run.ThreadId, update.TextAnnotation.GetType());
            }
        }

        if (update.Role.HasValue && update.Role.Value != MessageRole.User)
        {
            content.Role = new(update.Role.Value.ToString());
        }

        return content;
    }

    [ExcludeFromCodeCoverage]
    private static StreamingChatMessageContent? GenerateStreamingCodeInterpreterContent(string? assistantName, RunStepDetailsUpdate update)
    {
        StreamingChatMessageContent content =
            new(AuthorRole.Assistant, content: null)
            {
                AuthorName = assistantName,
            };

        // Process text content
        if (update.CodeInterpreterInput != null)
        {
            content.Items.Add(new StreamingTextContent(update.CodeInterpreterInput));
            content.Metadata = new Dictionary<string, object?> { { OpenAIAssistantAgent.CodeInterpreterMetadataKey, true } };
        }

        if ((update.CodeInterpreterOutputs?.Count ?? 0) > 0)
        {
            foreach (var output in update.CodeInterpreterOutputs!)
            {
                if (output.ImageFileId != null)
                {
                    content.Items.Add(new StreamingFileReferenceContent(output.ImageFileId));
                }
            }
        }

        return content.Items.Count > 0 ? content : null;
    }

    private static AnnotationContent? GenerateAnnotationContent(TextAnnotation annotation)
    {
        string referenceId;
        AnnotationKind kind;

        if (!string.IsNullOrEmpty(annotation.OutputFileId))
        {
            referenceId = annotation.OutputFileId;
            kind = AnnotationKind.TextCitation;
        }
        else if (!string.IsNullOrEmpty(annotation.InputFileId))
        {
            referenceId = annotation.InputFileId;
            kind = AnnotationKind.FileCitation;
        }
        else
        {
            return null;
        }

        return
            new(kind, label: annotation.TextToReplace, referenceId)
            {
                InnerContent = annotation,
                StartIndex = annotation.StartIndex,
                EndIndex = annotation.EndIndex,
            };
    }

    [ExcludeFromCodeCoverage]
    private static StreamingAnnotationContent? GenerateStreamingAnnotationContent(TextAnnotationUpdate annotation)
    {
        string referenceId;
        AnnotationKind kind;

        if (!string.IsNullOrEmpty(annotation.OutputFileId))
        {
            referenceId = annotation.OutputFileId;
            kind = AnnotationKind.TextCitation;
        }
        else if (!string.IsNullOrEmpty(annotation.InputFileId))
        {
            referenceId = annotation.InputFileId;
            kind = AnnotationKind.FileCitation;
        }
        else
        {
            return null;
        }

        return
            new(kind, referenceId)
            {
                Label = annotation.TextToReplace,
                InnerContent = annotation,
                StartIndex = annotation.StartIndex,
                EndIndex = annotation.EndIndex,
            };
    }

    private static ChatMessageContent GenerateCodeInterpreterContent(string agentName, string pythonCode, RunStep completedStep)
    {
        Dictionary<string, object?> metadata = GenerateToolCallMetadata(completedStep);
        metadata[OpenAIAssistantAgent.CodeInterpreterMetadataKey] = true;

        return
            new ChatMessageContent(
                AuthorRole.Assistant,
                [
                    new TextContent(pythonCode)
                ])
            {
                AuthorName = agentName,
                Metadata = metadata,
            };
    }

    private static IEnumerable<FunctionCallContent> ParseFunctionStep(OpenAIAssistantAgent agent, RunStep step)
    {
        if (step.Status == RunStepStatus.InProgress && step.Kind == RunStepKind.ToolCall)
        {
            foreach (RunStepToolCall toolCall in step.Details.ToolCalls)
            {
                (FunctionName nameParts, KernelArguments functionArguments) = ParseFunctionCall(toolCall.FunctionName, toolCall.FunctionArguments);

                FunctionCallContent content = new(nameParts.Name, nameParts.PluginName, toolCall.Id, functionArguments);

                yield return content;
            }
        }
    }

    private static (FunctionName functionName, KernelArguments arguments) ParseFunctionCall(string functionName, string? functionArguments)
    {
        FunctionName nameParts = FunctionName.Parse(functionName);

        KernelArguments arguments = [];

        if (!string.IsNullOrWhiteSpace(functionArguments))
        {
            foreach (var argumentKvp in JsonSerializer.Deserialize<Dictionary<string, object>>(functionArguments!)!)
            {
                arguments[argumentKvp.Key] = argumentKvp.Value.ToString();
            }
        }

        return (nameParts, arguments);
    }

    private static ChatMessageContent GenerateFunctionCallContent(string agentName, IList<FunctionCallContent> functionCalls)
    {
        ChatMessageContent functionCallContent = new(AuthorRole.Assistant, content: null)
        {
            AuthorName = agentName
        };

        functionCallContent.Items.AddRange(functionCalls);

        return functionCallContent;
    }

    private static ChatMessageContent GenerateFunctionResultContent(string agentName, IEnumerable<FunctionResultContent> functionResults, RunStep completedStep)
    {
        ChatMessageContent functionResultContent = new(AuthorRole.Tool, content: null)
        {
            AuthorName = agentName,
            Metadata = GenerateToolCallMetadata(completedStep),
        };

        foreach (FunctionResultContent functionResult in functionResults)
        {
            functionResultContent.Items.Add(
                new FunctionResultContent(
                    functionResult.FunctionName,
                    functionResult.PluginName,
                    functionResult.CallId,
                    functionResult.Result));
        }

        return functionResultContent;
    }

    private static Dictionary<string, object?> GenerateToolCallMetadata(RunStep completedStep)
    {
        return new()
            {
                { nameof(RunStep.CreatedAt), completedStep.CreatedAt },
                { nameof(RunStep.AssistantId), completedStep.AssistantId },
                { nameof(RunStep.ThreadId), completedStep.ThreadId },
                { nameof(RunStep.RunId), completedStep.RunId },
                { nameof(RunStepDetailsUpdate.StepId), completedStep.Id },
                { nameof(RunStep.Usage), completedStep.Usage },
            };
    }

    private static ToolOutput[] GenerateToolOutputs(FunctionResultContent[] functionResults)
    {
        ToolOutput[] toolOutputs = new ToolOutput[functionResults.Length];

        for (int index = 0; index < functionResults.Length; ++index)
        {
            FunctionResultContent functionResult = functionResults[index];

            object resultValue = functionResult.Result ?? string.Empty;

            if (resultValue is not string textResult)
            {
                textResult = JsonSerializer.Serialize(resultValue);
            }

            toolOutputs[index] = new ToolOutput(functionResult.CallId, textResult!);
        }

        return toolOutputs;
    }

    private static async Task<ThreadMessage?> RetrieveMessageAsync(AssistantClient client, string threadId, string messageId, TimeSpan syncDelay, CancellationToken cancellationToken)
    {
        ThreadMessage? message = null;

        bool retry = false;
        int count = 0;
        do
        {
            try
            {
                message = await client.GetMessageAsync(threadId, messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception)
            {
                // Step has provided the message-id.  Retry on of NotFound/404 exists.
                // Extremely rarely there might be a synchronization issue between the
                // assistant response and message-service.
                retry = exception.Status == (int)HttpStatusCode.NotFound && count < 3;
            }

            if (retry)
            {
                await Task.Delay(syncDelay, cancellationToken).ConfigureAwait(false);
            }

            ++count;
        }
        while (retry);

        return message;
    }
}
