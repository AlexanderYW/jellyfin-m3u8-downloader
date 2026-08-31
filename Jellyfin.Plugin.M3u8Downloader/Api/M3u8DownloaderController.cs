using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Model.Dto;
using Jellyfin.Plugin.M3u8Downloader.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.M3u8Downloader.Api;

/// <summary>
/// Queue management endpoints backing the plugin's dashboard page.
/// </summary>
/// <remarks>
/// Elevation is required for every endpoint. That is also what makes accepting arbitrary URLs
/// acceptable here: only server administrators can cause the server to fetch them.
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/M3u8Downloader")]
[Produces(MediaTypeNames.Application.Json)]
public class M3u8DownloaderController : ControllerBase
{
    private readonly IDownloadQueueService _queue;
    private readonly Func<PluginConfiguration> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="M3u8DownloaderController"/> class.
    /// </summary>
    /// <param name="queue">The download queue.</param>
    /// <param name="configuration">Reads the current plugin settings on each use.</param>
    public M3u8DownloaderController(IDownloadQueueService queue, Func<PluginConfiguration> configuration)
    {
        _queue = queue;
        _configuration = configuration;
    }

    /// <summary>
    /// Lists every job, newest first.
    /// </summary>
    /// <returns>The queue contents.</returns>
    /// <response code="200">Queue returned.</response>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<JobDto>> GetJobs()
    {
        return Ok(_queue.GetAll().Select(JobDto.FromJob).ToList());
    }

    /// <summary>
    /// Queues a batch of downloads parsed from bulk text.
    /// </summary>
    /// <param name="request">The bulk input.</param>
    /// <returns>What was queued and what was rejected.</returns>
    /// <response code="200">Batch processed; check the result for per-line rejections.</response>
    [HttpPost("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AddJobsResult> AddJobs([FromBody] AddJobsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _configuration();

        // Resolving here rather than at download time means a name that can never be written --
        // or an output directory nobody has configured yet -- is reported on the spot instead of
        // becoming a job that fails identically on every retry.
        var (accepted, rejected) = BulkInputParser.Parse(
            request.Text,
            request.Folder,
            name => ValidateName(config, name));

        _queue.AddRange(accepted);

        // A partially valid paste is not an error: the caller gets both halves and shows the
        // rejections inline rather than losing the whole batch.
        return Ok(new AddJobsResult
        {
            Added = accepted.Select(JobDto.FromJob).ToList(),
            Rejected = rejected,
        });
    }

    /// <summary>
    /// Checks that a requested name can be turned into a path under the output directory.
    /// </summary>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="requestedFileName">The name to check.</param>
    /// <returns>The reason the name is unusable, or <c>null</c> when it is fine.</returns>
    private static string? ValidateName(PluginConfiguration config, string requestedFileName)
    {
        try
        {
            // Never treats a name as taken: this is a validity check, and reserving a filename at
            // add time would collide with the deduplication the download itself performs.
            OutputPathResolver.Resolve(config.OutputDirectory, requestedFileName, _ => false);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ArgumentErrorText.Describe(ex);
        }
    }

    /// <summary>
    /// Cancels a running job, or removes a pending one.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Job cancelled or removed.</response>
    /// <response code="404">No such job.</response>
    [HttpDelete("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteJob([FromRoute] Guid jobId)
    {
        return _queue.CancelOrRemove(jobId) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Puts a failed or cancelled job back in the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Job re-queued.</response>
    /// <response code="404">No such job, or it is already queued or running.</response>
    [HttpPost("Jobs/{jobId}/Retry")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RetryJob([FromRoute] Guid jobId)
    {
        return _queue.Retry(jobId) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Moves a queued job to the front of the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Job moved to the front.</response>
    /// <response code="404">No such job, or it is not queued.</response>
    [HttpPost("Jobs/{jobId}/MoveToTop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult MoveJobToTop([FromRoute] Guid jobId)
    {
        return _queue.MoveToTop(jobId) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Moves a queued job to the back of the queue.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Job moved to the back.</response>
    /// <response code="404">No such job, or it is not queued.</response>
    [HttpPost("Jobs/{jobId}/MoveToBottom")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult MoveJobToBottom([FromRoute] Guid jobId)
    {
        return _queue.MoveToBottom(jobId) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Removes every completed, failed, and cancelled job.
    /// </summary>
    /// <returns>How many jobs were removed.</returns>
    /// <response code="200">Finished jobs cleared.</response>
    [HttpPost("Jobs/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> ClearFinished()
    {
        return Ok(_queue.ClearFinished());
    }
}
