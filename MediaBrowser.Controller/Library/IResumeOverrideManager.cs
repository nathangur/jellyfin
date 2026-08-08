using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Manages the per-user overrides that remove titles from the resume and next up lists.
/// </summary>
/// <remarks>
/// The two lists are controlled independently. Hiding an episode from resume leaves its series in
/// next up, and hiding a series from next up leaves its in-progress episodes in resume, so each
/// action does only what its name says.
/// </remarks>
public interface IResumeOverrideManager
{
    /// <summary>
    /// Removes an item from the user's resume list until they play it again.
    /// </summary>
    /// <remarks>
    /// Scoped to the item itself, so other in-progress episodes of the same series are unaffected.
    /// Repeating this for an item that is already hidden refreshes it.
    /// </remarks>
    /// <param name="userId">The user removing the item.</param>
    /// <param name="item">The item the user acted on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task HideFromResumeAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an item the user removed from the resume list.
    /// </summary>
    /// <remarks>Does nothing when the item is not hidden.</remarks>
    /// <param name="userId">The user restoring the item.</param>
    /// <param name="item">The item the user acted on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RestoreToResumeAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a series from the user's next up list until they play an episode of it again.
    /// </summary>
    /// <remarks>
    /// Next up is keyed on the series, so this is recorded against the series even when the user
    /// acted on an episode. In-progress episodes stay in the resume list.
    /// </remarks>
    /// <param name="userId">The user removing the series.</param>
    /// <param name="item">The item the user acted on, an episode, season or series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task HideFromNextUpAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a series the user removed from the next up list.
    /// </summary>
    /// <remarks>
    /// The series is resolved the same way as <see cref="HideFromNextUpAsync"/>, so undo works from
    /// any episode of it. Does nothing when the series is not hidden.
    /// </remarks>
    /// <param name="userId">The user restoring the series.</param>
    /// <param name="item">The item the user acted on, an episode, season or series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RestoreToNextUpAsync(Guid userId, BaseItem item, CancellationToken cancellationToken = default);
}
