namespace Platform.Api.Modules.Assets;

public sealed class AssetTagDuplicateException : InvalidOperationException
{
    public const string Code = "ASSET_TAG_DUPLICATE";

    public AssetTagDuplicateException(string? message = null)
        : base(message ?? "An asset with this tag already exists.")
    {
    }
}

public sealed class AssetInUseException : InvalidOperationException
{
    public const string Code = "ASSET_IN_USE";

    public AssetInUseException()
        : base("Cannot permanently delete an asset that is referenced by work orders.")
    {
    }
}

public sealed class CategoryInUseException : InvalidOperationException
{
    public const string Code = "CATEGORY_IN_USE";

    public CategoryInUseException(string message)
        : base(message)
    {
    }
}
