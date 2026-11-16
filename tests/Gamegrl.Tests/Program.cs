using Gamegrl.Tests;

CoreTests.Register();
VkLayoutTests.Register();
SceneTests.Register();
AssetTests.Register();
PrefabTests.Register();
return Harness.Run(args.Length > 0 ? args[0] : null);
