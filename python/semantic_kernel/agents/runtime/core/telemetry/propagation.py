# Copyright (c) Microsoft. All rights reserved.

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Optional

from opentelemetry.context import Context
from opentelemetry.propagate import extract
from opentelemetry.trace import Link, get_current_span
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator

from semantic_kernel.utils.feature_stage_decorator import experimental


@experimental
@dataclass(kw_only=True)
class EnvelopeMetadata:
    """Metadata for an envelope."""

    traceparent: str | None = None
    tracestate: str | None = None
    links: Sequence[Link] | None = None


def _get_carrier_for_envelope_metadata(envelope_metadata: EnvelopeMetadata) -> dict[str, str]:
    carrier: dict[str, str] = {}
    if envelope_metadata.traceparent is not None:
        carrier["traceparent"] = envelope_metadata.traceparent
    if envelope_metadata.tracestate is not None:
        carrier["tracestate"] = envelope_metadata.tracestate
    return carrier


@experimental
def get_telemetry_envelope_metadata() -> EnvelopeMetadata:
    """Retrieves the telemetry envelope metadata.

    Returns:
        EnvelopeMetadata: The envelope metadata containing the traceparent and tracestate.
    """
    carrier: dict[str, str] = {}
    TraceContextTextMapPropagator().inject(carrier)
    return EnvelopeMetadata(
        traceparent=carrier.get("traceparent"),
        tracestate=carrier.get("tracestate"),
    )


def _get_carrier_for_remote_call_metadata(remote_call_metadata: Mapping[str, str]) -> dict[str, str]:
    carrier: dict[str, str] = {}
    traceparent = remote_call_metadata.get("traceparent")
    tracestate = remote_call_metadata.get("tracestate")
    if traceparent:
        carrier["traceparent"] = traceparent
    if tracestate:
        carrier["tracestate"] = tracestate
    return carrier


@experimental
def get_telemetry_grpc_metadata(existingMetadata: Mapping[str, str] | None = None) -> dict[str, str]:
    """Retrieves the telemetry gRPC metadata.

    Args:
        existingMetadata (Optional[Mapping[str, str]]): The existing metadata to include in the gRPC metadata.

    Returns:
        Mapping[str, str]: The gRPC metadata containing the traceparent and tracestate.
    """
    carrier: dict[str, str] = {}
    TraceContextTextMapPropagator().inject(carrier)
    traceparent = carrier.get("traceparent")
    tracestate = carrier.get("tracestate")
    metadata: dict[str, str] = {}
    if existingMetadata is not None:
        for key, value in existingMetadata.items():
            metadata[key] = value
    if traceparent is not None:
        metadata["traceparent"] = traceparent
    if tracestate is not None:
        metadata["tracestate"] = tracestate
    return metadata


TelemetryMetadataContainer = Optional[EnvelopeMetadata] | Mapping[str, str]


@experimental
def get_telemetry_context(metadata: TelemetryMetadataContainer) -> Context:
    """Retrieves the telemetry context from the given metadata.

    Args:
        metadata (Optional[EnvelopeMetadata]): The metadata containing the telemetry context.

    Returns:
        Context: The telemetry context extracted from the metadata, or an empty context if the metadata is None.
    """
    if metadata is None:
        return Context()
    if isinstance(metadata, EnvelopeMetadata):
        return extract(_get_carrier_for_envelope_metadata(metadata))
    if hasattr(metadata, "__getitem__"):
        return extract(_get_carrier_for_remote_call_metadata(metadata))
    raise ValueError(f"Unknown metadata type: {type(metadata)}")


@experimental
def get_telemetry_links(
    metadata: TelemetryMetadataContainer,
) -> Sequence[Link] | None:
    """Retrieves the telemetry links from the given metadata.

    Args:
        metadata (Optional[EnvelopeMetadata]): The metadata containing the telemetry links.

    Returns:
        Optional[Sequence[Link]]: The telemetry links extracted from the metadata, or None if there are no links.
    """
    if metadata is None:
        return None
    if isinstance(metadata, EnvelopeMetadata):
        context = extract(_get_carrier_for_envelope_metadata(metadata))
    elif hasattr(metadata, "__getitem__"):
        context = extract(_get_carrier_for_remote_call_metadata(metadata))
    else:
        return None
    # Retrieve the extracted SpanContext from the context.
    linked_span = get_current_span(context)
    # Use the linked span to get the SpanContext.
    span_context = linked_span.get_span_context()
    # Create a Link object using the SpanContext.
    return [Link(span_context)]
