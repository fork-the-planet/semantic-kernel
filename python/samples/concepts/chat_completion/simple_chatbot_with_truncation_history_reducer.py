# Copyright (c) Microsoft. All rights reserved.

import asyncio

from samples.concepts.setup.chat_completion_services import (
    Services,
    get_chat_completion_service_and_request_settings,
)
from semantic_kernel import Kernel
from semantic_kernel.contents import ChatHistoryTruncationReducer
from semantic_kernel.functions import KernelArguments

# This sample shows how to create a chatbot using a kernel function and leverage a chat history
# truncation reducer.
# This sample uses the following two main components:
# - a ChatCompletionService: This component is responsible for generating responses to user messages.
# - a Chat History Reducer: This component is responsible for keeping track and reducing the chat history.
#                           A Chat History Reducer is a subclass of ChatHistory that provides additional
#                           functionality to reduce the history.
# - a KernelFunction: This function will be a prompt function, meaning the function is composed of
#                     a prompt and will be invoked by Semantic Kernel.
# The chatbot in this sample is called Mosscap, who responds to user messages with long flowery prose.

# [NOTE]
# The purpose of this sample is to demonstrate how to use a kernel function and use a chat history reducer.
# To build a basic chatbot, it is sufficient to use a ChatCompletionService with a chat history directly.

# You can select from the following chat completion services:
# - Services.OPENAI
# - Services.AZURE_OPENAI
# - Services.AZURE_AI_INFERENCE
# - Services.ANTHROPIC
# - Services.BEDROCK
# - Services.GOOGLE_AI
# - Services.MISTRAL_AI
# - Services.OLLAMA
# - Services.ONNX
# - Services.VERTEX_AI
# Please make sure you have configured your environment correctly for the selected chat completion service.
chat_completion_service, request_settings = get_chat_completion_service_and_request_settings(Services.AZURE_OPENAI)

# This is the system message that gives the chatbot its personality.
system_message = """
You are a chat bot. Your name is Mosscap and
you have one goal: figure out what people need.
Your full name, should you need to know it, is
Splendid Speckled Mosscap. You communicate
effectively, but you tend to answer with long
flowery prose.
"""

# Create a kernel and register a prompt function.
# The prompt here contains two variables: chat_history and user_input.
# They will be replaced by the kernel with the actual values when the function is invoked.
# [NOTE]
# The chat_history, which is a ChatHistory object, will be serialized to a string internally
# to create/render the final prompt.
# Since this sample uses a chat completion service, the prompt will be deserialized back to
# a ChatHistory object that gets passed to the chat completion service. This new chat history
# object will contain the original messages and the user input.
kernel = Kernel()
chat_function = kernel.add_function(
    plugin_name="ChatBot",
    function_name="Chat",
    prompt="{{$chat_history}}{{$user_input}}",
    template_format="semantic-kernel",
    # You can attach the request settings to the function or
    # pass the settings to the kernel.invoke method via the kernel arguments.
    # If you specify the settings in both places, the settings in the kernel arguments will
    # take precedence given the same service id.
    # prompt_execution_settings=request_settings,
)

# Invoking a kernel function requires a service, so we add the chat completion service to the kernel.
kernel.add_service(chat_completion_service)

# The chat history reducer is responsible for truncating the chat history.
# It's a subclass of ChatHistory that provides additional functionality to reduce the history.
# You may use it just like a regular ChatHistory object.
truncation_reducer = ChatHistoryTruncationReducer(
    service=kernel.get_service(),
    # target_count:
    # Purpose: Defines the target number of messages to retain after applying summarization.
    # What it controls: This parameter determines how much of the most recent conversation history
    #                   is preserved while discarding or summarizing older messages.
    # Why change it?:
    # - Smaller values: Use when memory constraints are tight, or the assistant only needs a brief history
    #   to maintain context.
    # - Larger values: Use when retaining more conversational context is critical for accurate responses
    #   or maintaining a richer dialogue.
    target_count=3,
    # threshold_count:
    # Purpose: Acts as a buffer to avoid reducing history prematurely when the current message count exceeds
    #          target_count by a small margin.
    # What it controls: Helps ensure that essential paired messages (like a user query and the assistant’s response)
    #                   are not "orphaned" or lost during truncation or summarization.
    # Why change it?:
    # - Smaller values: Use when you want stricter reduction criteria and are okay with possibly cutting older
    #   pairs of messages sooner.
    # - Larger values: Use when you want to minimize the risk of cutting a critical part of the conversation,
    #   especially for sensitive interactions like API function calls or complex responses.
    threshold_count=2,
)

truncation_reducer.add_system_message(system_message)


async def chat() -> bool:
    try:
        user_input = input("User:> ")
    except KeyboardInterrupt:
        print("\n\nExiting chat...")
        return False
    except EOFError:
        print("\n\nExiting chat...")
        return False

    if user_input == "exit":
        print("\n\nExiting chat...")
        return False

    # Attempt to reduce before adding the user message to the chat history.
    await truncation_reducer.reduce()

    # Get the chat message content from the chat completion service.
    kernel_arguments = KernelArguments(
        settings=request_settings,
        # Use keyword arguments to pass the chat history and user input to the kernel function.
        chat_history=truncation_reducer,
        user_input=user_input,
    )

    answer = await kernel.invoke(plugin_name="ChatBot", function_name="Chat", arguments=kernel_arguments)
    # Alternatively, you can invoke the function directly with the kernel as an argument:
    # answer = await chat_function.invoke(kernel, kernel_arguments)
    if answer:
        print(f"Mosscap:> {answer}")
        # Since the user_input is rendered by the template, it is not yet part of the chat history, so we add it here.
        truncation_reducer.add_user_message(user_input)
        # Add the chat message to the chat history to keep track of the conversation.
        truncation_reducer.add_message(answer.value[0])

    return True


async def main() -> None:
    # Start the chat loop. The chat loop will continue until the user types "exit".
    chatting = True
    while chatting:
        chatting = await chat()

    # Sample output:
    # User:> Why is the sky blue in one sentence?
    # Mosscap:> The sky is blue due to the scattering of sunlight by the molecules in the Earth's atmosphere,
    #           a phenomenon known as Rayleigh scattering, which causes shorter blue wavelengths to become more
    #           prominent in our visual perception.


if __name__ == "__main__":
    asyncio.run(main())