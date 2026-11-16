namespace Gamegrl.Assets;

/// <summary>
/// Loads a startup scene from the asset source when the app starts: what a player does. Game modules register
/// systems and resources only; the player (or the editor) decides which scene is open.
/// </summary>
public sealed class SceneBootModule(string scenePath) : IModule
{
    public void Build(EngineBuilder builder) => builder.OnStart(app =>
    {
        var assets = app.World.Resource<IAssetSource>();
        var guid = assets.FindByPath(scenePath);
        if (!assets.TryRead(guid, out var kind, out var bytes) || kind != AssetKind.Scene)
            throw new InvalidOperationException($"{scenePath} is missing from the asset source.");
        var loaded = Scene.LoadBinary(app.World, bytes.Span, w => Log.Warn("scene: " + w));
        Log.Info($"loaded {scenePath}: {loaded.Length} entities");
    });
}
