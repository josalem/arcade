// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.DotNet.VersionTools.BuildManifest.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MSBuild = Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    public class PublishToFeed : MSBuild.Task
    {
        [Required]
        public string ExpectedFeedUrl { get; set; }

        [Required]
        public string AccountKey { get; set; }

        public bool Overwrite { get; set; }

        /// <summary>
        /// Enables idempotency when Overwrite is false.
        /// 
        /// false: (default) Attempting to upload an item that already exists fails.
        /// 
        /// true: When an item already exists, download the existing blob to check if it's
        /// byte-for-byte identical to the one being uploaded. If so, pass. If not, fail.
        /// </summary>
        public bool PassIfExistingItemIdentical { get; set; }

        public int MaxClients { get; set; } = 8;

        public int UploadTimeoutInMinutes { get; set; } = 5;

        [Required]
        public string AssetManifestPath { get; set; }

        public string BlobAssetsBasePath { get; set; }

        public string PackageAssetsBasePath { get; set; }

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> ExecuteAsync()
        {
            try
            {
                Log.LogMessage(MessageImportance.High, "Performing feed push...");

                if (string.IsNullOrEmpty(AssetManifestPath) || !File.Exists(AssetManifestPath))
                {
                    Log.LogError($"Problem reading asset manifest path from {AssetManifestPath}");
                }
                else
                {
                    BlobFeedAction blobFeedAction = new BlobFeedAction(ExpectedFeedUrl, AccountKey, Log);

                    var buildModel = BuildManifestUtil.ManifestFileToModel(AssetManifestPath, Log);

                    var packages = buildModel.Artifacts.Packages.Select(p => $"{PackageAssetsBasePath}{p.Id}.{p.Version}.nupkg");
                    var symbols = buildModel.Artifacts.Blobs.Select(p => $"{BlobAssetsBasePath}{p.Id}");

                    await blobFeedAction.PushToFeedAsync(packages, CreatePushOptions());
                    await PublishToFlatContainerAsync(symbols, blobFeedAction);
                }
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true);
            }

            return !Log.HasLoggedErrors;
        }

        private async Task PublishToFlatContainerAsync(IEnumerable<string> taskItems, BlobFeedAction blobFeedAction)
        {
            if (taskItems.Any())
            {
                using (var clientThrottle = new SemaphoreSlim(this.MaxClients, this.MaxClients))
                {
                    Log.LogMessage(MessageImportance.High, $"Uploading {taskItems.Count()} items:");
                    await Task.WhenAll(taskItems.Select(
                        item =>
                        {
                            Log.LogMessage(MessageImportance.High, $"Async uploading {item.ItemSpec}");
                            return blobFeedAction.UploadAssetAsync(
                                item,
                                clientThrottle,
                                UploadTimeoutInMinutes,
                                CreatePushOptions());
                        }
                    ));
                }
            }
        }

        private PushOptions CreatePushOptions()
        {
            return new PushOptions
            {
                AllowOverwrite = Overwrite,
                PassIfExistingItemIdentical = PassIfExistingItemIdentical
            };
        }
    }
}
