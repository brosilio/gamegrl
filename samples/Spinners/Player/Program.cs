using Gamegrl.Assets;
using Spinners;

// The shipping player: everything comes from spinners.ggpak, memory-mapped from beside the executable.
string pak = Path.Combine(AppContext.BaseDirectory, "spinners.ggpak");
if (!File.Exists(pak))
{
    Console.Error.WriteLine($"spinners: {pak} not found (build it with: dotnet run --project samples/Spinners/Build -- pack <path>)");
    return 1;
}

using var app = SpinnersApp.Build(new AssetArchive(pak), SpinnersApp.ParseRunFor(args));
SpinnersApp.Run(app);
return 0;
