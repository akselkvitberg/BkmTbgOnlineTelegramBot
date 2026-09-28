namespace EventPhotoBot.State;

public static class ImageObjects
{
    /// <summary>
    /// An image's three objects, by id and original extension. Called after the state
    /// write that removed its record: a record pointing at deleted bytes would put a
    /// broken image on the projector, while bytes nothing points at are invisible.
    /// </summary>
    public static async Task DeleteAsync(
        IObjectStore objects, string id, string originalExtension, CancellationToken ct = default)
    {
        await objects.DeleteAsync(ObjectPaths.Display(id), ct);
        await objects.DeleteAsync(ObjectPaths.Thumb(id), ct);
        await objects.DeleteAsync(ObjectPaths.Original(id, originalExtension), ct);
    }

    public static Task DeleteAsync(IObjectStore objects, ImageRecord image, CancellationToken ct = default) =>
        DeleteAsync(objects, image.Id, image.OriginalExtension, ct);

    /// <summary>
    /// Deletes every image's objects, one at a time, best effort: CancellationToken.None
    /// so an aborted request — a browser tab closed mid-export-sized delete, a dropped
    /// connection, Cloud Scheduler's attempt deadline — does not leave bytes behind with
    /// no record left pointing at them to retry the delete later, and each image is
    /// isolated in its own try/catch so one failing GCS call does not stop the rest of
    /// the batch from being cleaned up. Call only after the state write that removed
    /// these records — see DeleteAsync above.
    /// </summary>
    public static async Task DeleteAllAsync(IObjectStore objects, IReadOnlyList<ImageRecord> images, ILogger logger)
    {
        foreach (var image in images)
        {
            try
            {
                await DeleteAsync(objects, image, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to delete objects for image {ImageId}; its bytes are now orphaned.", image.Id);
            }
        }
    }
}
