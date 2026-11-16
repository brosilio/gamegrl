using Gamegrl.Tests;

CoreTests.Register();
InputTests.Register();
VkLayoutTests.Register();
SceneTests.Register();
MetadataTests.Register();
AssetTests.Register();
PrefabTests.Register();
return Harness.Run(args.Length > 0 ? args[0] : null);
