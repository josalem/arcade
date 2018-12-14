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

        /// <summary>
        /// Full path to the assets to publish manifest.
        /// </summary>
        [Required]
        public string AssetManifestPath { get; set; }

        /// <summary>
        /// Full path to the folder containing blob assets.
        /// </summary>
        public string BlobAssetsBasePath { get; set; }

        /// <summary>
        /// Full path to the folder containing package assets.
        /// </summary>
        public string PackageAssetsBasePath { get; set; }

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> ExecuteAsync()
        {
            try
            {
                Log.LogMessage(MessageImportance.High, "Performing push feeds.");

                if (string.IsNullOrEmpty(AssetManifestPath) || !File.Exists(AssetManifestPath))
                {
                    Log.LogError($"Problem reading asset manifest path from {AssetManifestPath}");
                }
                else if (string.IsNullOrEmpty(PackageAssetsBasePath) && string.IsNullOrEmpty(BlobAssetsBasePath))
                {
                    Log.LogError($"Base path for package and assets is invalid.");
                }

                PackageAssetsBasePath = PackageAssetsBasePath.EndsWith(Path.DirectorySeparatorChar) ?
                    PackageAssetsBasePath : PackageAssetsBasePath + Path.DirectorySeparatorChar;

                BlobAssetsBasePath = BlobAssetsBasePath.EndsWith(Path.DirectorySeparatorChar) ?
                    BlobAssetsBasePath : BlobAssetsBasePath + Path.DirectorySeparatorChar;

                BlobFeedAction blobFeedAction = new BlobFeedAction(ExpectedFeedUrl, AccountKey, Log);

                var buildModel = BuildManifestUtil.ManifestFileToModel(AssetManifestPath, Log);

                var packages = buildModel.Artifacts.Packages.Select(p => $"{PackageAssetsBasePath}{p.Id}.{p.Version}.nupkg");
                var blobs = buildModel.Artifacts.Blobs.Select(p => $"{BlobAssetsBasePath}{p.Id}");

                await blobFeedAction.PushToFeedAsync(packages, CreatePushOptions());
                await PublishToFlatContainerAsync(blobs, blobFeedAction);
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true);
            }

            return !Log.HasLoggedErrors;
        }

        private async Task PublishToFlatContainerAsync(IEnumerable<string> blobPaths, BlobFeedAction blobFeedAction)
        {
            if (blobPaths.Any())
            {
                using (var clientThrottle = new SemaphoreSlim(this.MaxClients, this.MaxClients))
                {
                    Log.LogMessage(MessageImportance.High, $"Uploading {blobPaths.Count()} items:");
                    await Task.WhenAll(blobPaths.Select(
                        item =>
                        {
                            Log.LogMessage(MessageImportance.High, $"Async uploading {item}");
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
