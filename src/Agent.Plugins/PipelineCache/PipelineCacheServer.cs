// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agent.Plugins.PipelineArtifact;
using Agent.Plugins.PipelineCache.Telemetry;
using Agent.Sdk;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using JsonSerializer = Microsoft.VisualStudio.Services.Content.Common.JsonSerializer;

namespace Agent.Plugins.PipelineCache
{
    public class PipelineCacheServer
    {
        internal async Task UploadAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint keyFingerprint,
            string[] pathSegments,
            string workspaceRoot,
            CancellationToken cancellationToken,
            ContentFormat contentFormat)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.Instance.CreateDedupManifestClient(context, connection, cancellationToken, out clientTelemetry);
            PipelineCacheClient pipelineCacheClient = this.CreateClient(clientTelemetry, context, connection);

            using (clientTelemetry)
            {
                // Check if the key exists.
                PipelineCacheActionRecord cacheRecordGet = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.RestoreCache, context));
                PipelineCacheArtifact getResult = await pipelineCacheClient.GetPipelineCacheArtifactAsync(new[] { keyFingerprint }, cancellationToken, cacheRecordGet);
                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecordGet);
                //If cache exists, return.
                if (getResult != null)
                {
                    context.Output($"Cache with fingerprint `{getResult.Fingerprint}` already exists.");
                    return;
                }

                context.Output("Resolving path:");
                Fingerprint pathFp = FingerprintCreator.EvaluateToFingerprint(context, workspaceRoot, pathSegments, FingerprintType.Path);
                context.Output($"Resolved to: {pathFp}");

                string uploadPath = await this.GetUploadPathAsync(contentFormat, context, pathFp, workspaceRoot, cancellationToken);

                //Upload the pipeline artifact.
                PipelineCacheActionRecord uploadRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                    new PipelineCacheActionRecord(level, uri, type, nameof(dedupManifestClient.PublishAsync), context));
                PublishResult result = await clientTelemetry.MeasureActionAsync(
                    record: uploadRecord,
                    actionAsync: async () =>
                    {
                        return await dedupManifestClient.PublishAsync(uploadPath, cancellationToken);
                    });

                CreatePipelineCacheArtifactContract options = new CreatePipelineCacheArtifactContract
                {
                    Fingerprint = keyFingerprint,
                    RootId = result.RootId,
                    ManifestId = result.ManifestId,
                    ProofNodes = result.ProofNodes.ToArray(),
                    ContentFormat = contentFormat.ToString(),
                };

                // delete archive file if it's tar.
                if (contentFormat == ContentFormat.SingleTar)
                {
                    try
                    {
                        if (File.Exists(uploadPath))
                        {
                            File.Delete(uploadPath);
                        }
                    }
                    catch { }
                }

                // Cache the artifact
                PipelineCacheActionRecord cacheRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                    new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.SaveCache, context));
                CreateStatus status = await pipelineCacheClient.CreatePipelineCacheArtifactAsync(options, cancellationToken, cacheRecord);

                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: uploadRecord);
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecord);
                context.Output("Saved item.");
            }
        }

        internal async Task DownloadAsync(
            AgentTaskPluginExecutionContext context,
            Fingerprint[] fingerprints,
            string[] pathSegments,
            string cacheHitVariable,
            string workspaceRoot,
            string dryRun,
            CancellationToken cancellationToken)
        {
            VssConnection connection = context.VssConnection;
            BlobStoreClientTelemetry clientTelemetry;
            DedupManifestArtifactClient dedupManifestClient = DedupManifestArtifactClientFactory.Instance.CreateDedupManifestClient(context, connection, cancellationToken, out clientTelemetry);
            PipelineCacheClient pipelineCacheClient = this.CreateClient(clientTelemetry, context, connection);

            using (clientTelemetry)
            {
                PipelineCacheActionRecord cacheRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, PipelineArtifactConstants.RestoreCache, context));
                PipelineCacheArtifact result = await pipelineCacheClient.GetPipelineCacheArtifactAsync(fingerprints, cancellationToken, cacheRecord);

                // Send results to CustomerIntelligence
                context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: cacheRecord);

                if (dryRun == "true")
                {
                    context.Output(result != null ? "Cache exists." : "Cache doesn't exist");
                }
                else if (result != null)
                {
                    context.Output($"Entry found at fingerprint: `{result.Fingerprint.ToString()}`");
                    context.Verbose($"Manifest ID is: {result.ManifestId.ValueString}");
                    PipelineCacheActionRecord downloadRecord = clientTelemetry.CreateRecord<PipelineCacheActionRecord>((level, uri, type) =>
                        new PipelineCacheActionRecord(level, uri, type, nameof(DownloadAsync), context));
                    await clientTelemetry.MeasureActionAsync(
                        record: downloadRecord,
                        actionAsync: async () =>
                        {
                            await this.DownloadPipelineCacheAsync(context, dedupManifestClient, result.ManifestId, pathSegments, workspaceRoot, Enum.Parse<ContentFormat>(result.ContentFormat), cancellationToken);
                        });

                    // Send results to CustomerIntelligence
                    context.PublishTelemetry(area: PipelineArtifactConstants.AzurePipelinesAgent, feature: PipelineArtifactConstants.PipelineCache, record: downloadRecord);

                    context.Output("Cache restored.");
                }

                if (!string.IsNullOrEmpty(cacheHitVariable))
                {
                    if (result == null)
                    {
                        context.SetVariable(cacheHitVariable, "false");
                    }
                    else
                    {
                        context.Verbose($"Exact fingerprint: `{result.Fingerprint.ToString()}`");

                        bool foundExact = false;
                        foreach (var fingerprint in fingerprints)
                        {
                            context.Verbose($"This fingerprint: `{fingerprint.ToString()}`");

                            if (fingerprint == result.Fingerprint
                                || result.Fingerprint.Segments.Length == 1 && result.Fingerprint.Segments.Single() == fingerprint.SummarizeForV1())
                            {
                                foundExact = true;
                                break;
                            }
                        }

                        context.SetVariable(cacheHitVariable, foundExact ? "true" : "inexact");
                    }
                }
            }
        }

        private PipelineCacheClient CreateClient(
            BlobStoreClientTelemetry blobStoreClientTelemetry,
            AgentTaskPluginExecutionContext context,
            VssConnection connection)
        {
            var tracer = new CallbackAppTraceSource(str => context.Output(str), System.Diagnostics.SourceLevels.Information);
            IClock clock = UtcClock.Instance;
            var pipelineCacheHttpClient = connection.GetClient<PipelineCacheHttpClient>();
            var pipelineCacheClient = new PipelineCacheClient(blobStoreClientTelemetry, pipelineCacheHttpClient, clock, tracer);

            return pipelineCacheClient;
        }

        private async Task<string> GetUploadPathAsync(ContentFormat contentFormat, AgentTaskPluginExecutionContext context, Fingerprint pathFingerprint, string workspaceRoot, CancellationToken cancellationToken)
        {
            if (pathFingerprint.Segments.Length == 1)
            {
                return pathFingerprint.Segments[0];
            }
            if (contentFormat == ContentFormat.SingleTar)
            {
                return await TarUtils.ArchiveFilesToTarAsync(context, pathFingerprint, workspaceRoot, cancellationToken);
            }
            // TODO: what is the right way to handle !ContentFormat.SingleTar
            return pathFingerprint.Segments[0];
        }

        private async Task DownloadPipelineCacheAsync(
            AgentTaskPluginExecutionContext context,
            DedupManifestArtifactClient dedupManifestClient,
            DedupIdentifier manifestId,
            string[] pathSegments,
            string workspaceRoot,
            ContentFormat contentFormat,
            CancellationToken cancellationToken)
        {
            if (contentFormat == ContentFormat.SingleTar)
            {
                string manifestPath = Path.Combine(Path.GetTempPath(), $"{nameof(DedupManifestArtifactClient)}.{Path.GetRandomFileName()}.manifest");
                await dedupManifestClient.DownloadFileToPathAsync(manifestId, manifestPath, proxyUri: null, cancellationToken: cancellationToken);
                Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
                await TarUtils.DownloadAndExtractTarAsync(context, manifest, dedupManifestClient, workspaceRoot, cancellationToken);
                try
                {
                    if (File.Exists(manifestPath))
                    {
                        File.Delete(manifestPath);
                    }
                }
                catch { }
            }
            else
            {
                DownloadDedupManifestArtifactOptions options = DownloadDedupManifestArtifactOptions.CreateWithManifestId(
                    manifestId,
                    pathSegments[0], // TODO: is this the right format here
                    proxyUri: null,
                    minimatchPatterns: null);
                await dedupManifestClient.DownloadAsync(options, cancellationToken);
            }

        }
    }
}