﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Squirrel.Json;

namespace Squirrel.Sources
{
    /// <summary>
    /// Describes a Gitlab release, plus any assets that are attached.
    /// </summary>
    [DataContract]
    public class GitlabRelease
    {
        /// <summary>
        /// The name of the release.
        /// </summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>
        /// True if this is intended for an upcoming release.
        /// </summary>
        [DataMember(Name = "upcoming_release")]
        public bool UpcomingRelease { get; set; }

        /// <summary>
        /// The date which this release was published publically.
        /// </summary>
        [DataMember(Name = "released_at")]
        public DateTime ReleasedAt { get; set; }

        /// <summary>
        /// A container for the assets (files) uploaded to this release.
        /// </summary>
        [DataMember(Name = "assets")]
        public GitlabReleaseAsset Assets { get; set; }
    }

    /// <summary>
    /// Describes a container for the assets attached to a release.
    /// </summary>
    [DataContract]
    public class GitlabReleaseAsset
    {
        /// <summary>
        /// The amount of assets linked to the release.
        /// </summary>
        [DataMember(Name = "count")]
        public int Count { get; set; }

        /// <summary>
        /// A list of asset (file) links.
        /// </summary>
        [DataMember(Name = "links")]
        public GitlabReleaseLink[] Links { get; set; }
    }

    /// <summary>
    /// Describes a container for the links of assets attached to a release.
    /// </summary>
    [DataContract]
    public class GitlabReleaseLink
    {
        /// <summary>
        /// Name of the asset (file) linked.
        /// </summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>
        /// The url for the asset. This make use of the Gitlab API.
        /// </summary>
        [DataMember(Name = "url")]
        public string Url { get; set; }

        /// <summary>
        /// A direct url to the asset, via a traditional URl. 
        /// As a posed to using the API.
        /// This links directly to the raw asset (file).
        /// </summary>
        [DataMember(Name = "direct_asset_url")]
        public string DirectAssetUrl { get; set; }

        /// <summary>
        /// The category type that the asset is listed under.
        /// Options: 'Package', 'Image', 'Runbook', 'Other'
        /// </summary>
        [DataMember(Name = "link_type")]
        public string Type { get; set; }
    }

    /// <summary>
    /// Retrieves available releases from a GitLab repository. This class only
    /// downloads assets from the very latest GitLab release.
    /// </summary>
    public class GitlabSource : IUpdateSource
    {
        /// <summary> 
        /// The URL of the GitLab repository to download releases from 
        /// (e.g. https://gitlab.com/api/v4/projects/ProjectId)
        /// </summary>
        public virtual Uri RepoUri { get; }

        /// <summary>  
        /// If true, the latest upcoming release will be downloaded. If false, the latest 
        /// stable release will be downloaded.
        /// </summary>
        public virtual bool UpcomingRelease { get; }

        /// <summary> 
        /// The file downloader used to perform HTTP requests. 
        /// </summary>
        public virtual IFileDownloader Downloader { get; }

        /// <summary>  
        /// The GitLab release which this class should download assets from when 
        /// executing <see cref="DownloadReleaseEntry"/>. This property can be set
        /// explicitly, otherwise it will also be set automatically when executing
        /// <see cref="GetReleaseFeed(Guid?, ReleaseEntry)"/>.
        /// </summary>
        public virtual GitlabRelease Release { get; set; }

        /// <summary>
        /// The GitLab access token to use with the request to download releases.
        /// </summary>
        protected virtual string AccessToken { get; }

        /// <summary>
        /// The Bearer token used in the request.
        /// </summary>
        protected virtual string Authorization => string.IsNullOrWhiteSpace(AccessToken) ? null : "Bearer " + AccessToken;

        /// <inheritdoc cref="GitlabSource" />
        /// <param name="repoUrl">
        /// The URL of the GitLab repository to download releases from 
        /// (e.g. https://gitlab.com/api/v4/projects/ProjectId)
        /// </param>
        /// <param name="accessToken">
        /// The GitLab access token to use with the request to download releases.
        /// </param>
        /// <param name="upcomingRelease">
        /// If true, the latest upcoming release will be downloaded. If false, the latest 
        /// stable release will be downloaded.
        /// </param>
        /// <param name="downloader">
        /// The file downloader used to perform HTTP requests. 
        /// </param>
        public GitlabSource(string repoUrl, string accessToken, bool upcomingRelease, IFileDownloader downloader = null)
        {
            RepoUri = new Uri(repoUrl);
            AccessToken = accessToken;
            UpcomingRelease = upcomingRelease;
            Downloader = downloader ?? Utility.CreateDefaultDownloader();
        }

        /// <inheritdoc />
        public Task DownloadReleaseEntry(ReleaseEntry releaseEntry, string localFile, Action<int> progress)
        {
            if (Release == null) {
                throw new InvalidOperationException("No GitLab Release specified. Call GetReleaseFeed or set " +
                    "GitLabSource.Release before calling this function.");
            }

            var assetUrl = GetAssetUrlFromName(Release, releaseEntry.Filename);
            return Downloader.DownloadFile(assetUrl, localFile, progress, Authorization, "application/octet-stream");
        }

        /// <inheritdoc />
        public async Task<ReleaseEntry[]> GetReleaseFeed(Guid? stagingId = null, ReleaseEntry latestLocalRelease = null)
        {
            var releases = await GetReleases(UpcomingRelease).ConfigureAwait(false);
            if (releases == null || releases.Count() == 0)
                throw new Exception($"No Gitlab releases found at '{RepoUri}'.");

            // CS: we 'cache' the release here, so subsequent calls to DownloadReleaseEntry
            // will download assets from the same release in which we returned ReleaseEntry's
            // from. A better architecture would be to return an array of "GitlabReleaseEntry"
            // containing a reference to the GitlabReleaseAsset instead.
            Release = releases.First();

            var assetUrl = GetAssetUrlFromName(Release, "RELEASES");
            var releaseBytes = await Downloader.DownloadBytes(assetUrl, Authorization, "application/octet-stream").ConfigureAwait(false);
            var txt = Utility.RemoveByteOrderMarkerIfPresent(releaseBytes);
            return ReleaseEntry.ParseReleaseFileAndApplyStaging(txt, stagingId).ToArray();
        }

        /// <summary>
        /// Given a <see cref="GitlabRelease"/> and an asset filename (eg. 'RELEASES') this 
        /// function will return either <see cref="GitlabReleaseLink.DirectAssetUrl"/> or
        /// <see cref="GitlabReleaseLink.Url"/>, depending whether an access token is available
        /// or not. Throws if the specified release has no matching assets.
        /// </summary>
        protected virtual string GetAssetUrlFromName(GitlabRelease release, string assetName)
        {
            if (release.Assets == null || release.Assets.Count == 0) 
            {
                throw new ArgumentException($"No assets found in Gitlab Release '{release.Name}'.");
            }

            GitlabReleaseLink packageFile =
                release.Assets.Links.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.InvariantCultureIgnoreCase));
            if (packageFile == null) 
            {
                throw new ArgumentException($"Could not find asset called '{assetName}' in GitLab Release '{release.Name}'.");
            }

            if (String.IsNullOrWhiteSpace(AccessToken)) 
            {
                return packageFile.DirectAssetUrl;
            } 
            else 
            {
                return packageFile.Url;
            }
        }

        /// <summary>
        /// Retrieves a list of <see cref="GitlabRelease"/> from the current repository.
        /// </summary>
        public virtual async Task<GitlabRelease[]> GetReleases(bool includePrereleases, int perPage = 30, int page = 1)
        {
            // https://docs.gitlab.com/ee/api/releases/
            var releasesPath = $"{RepoUri.AbsolutePath}/releases?per_page={perPage}&page={page}";
            var baseUri = new Uri("https://gitlab.com");
            var getReleasesUri = new Uri(baseUri, releasesPath);
            var response = await Downloader.DownloadString(getReleasesUri.ToString(), Authorization).ConfigureAwait(false);
            var releases = SimpleJson.DeserializeObject<List<GitlabRelease>>(response);
            return releases.OrderByDescending(d => d.ReleasedAt).Where(x => includePrereleases || !x.UpcomingRelease).ToArray();
        }
    }
}
