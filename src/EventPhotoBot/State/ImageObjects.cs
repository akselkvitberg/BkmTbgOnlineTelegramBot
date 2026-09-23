namespace EventPhotoBot.State;

public static class ImageObjects
{
    /// <summary>
    /// An image's three objects. Called after the state write that removed its record:
    /// a record pointing at deleted bytes would put a broken image on the projector,
    /// while bytes nothing points at are invisible.
    /// </summary>
    public static async Task DeleteAsync(IObjectStore objects, ImageRecord image, CancellationToken ct = default)
    {
        await objects.DeleteAsync(ObjectPaths.Display(image.Id), ct);
        await objects.DeleteAsync(ObjectPaths.Thumb(image.Id), ct);
        await objects.DeleteAsync(ObjectPaths.Original(image.Id, image.OriginalExtension), ct);
    }
}
