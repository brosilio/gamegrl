using Gamegrl.Tests;

CoreTests.Register();
InputTests.Register();
ResourceTests.Register();
VkLayoutTests.Register();
SceneTests.Register();
MetadataTests.Register();
AssetTests.Register();
PrefabTests.Register();
UiTests.Register();
ProfilerTests.Register();
DeterminismTests.Register();
Physics2DTests.Register();
Physics2DWorldTests.Register();
AudioTests.Register();
AudioSpatialTests.Register();
AcousticsTests.Register();
return Harness.Run(args.Length > 0 ? args[0] : null);
