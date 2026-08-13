using InnoUnpack.NET;

namespace InnoUnpack.Tests;

/// <summary>
///     完整元数据解析测试：验证脚本条目（Registry/Run/Icon/INI/Task/Component/Type/Message 等）
///     的解析数量与头部计数一致（数量不符即说明解析错位）。
/// </summary>
public class MetadataTests {
	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.5.9-unicode.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	[InlineData("innosetup-7.0.2-x64.exe")]
	public void MetadataCountsMatchHeader(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var info = archive.Info;
		var h = info.Header;

		Assert.Equal(h.MessageCount, info.Messages.Count);
		Assert.Equal(h.PermissionCount, info.Permissions.Count);
		Assert.Equal(h.TypeCount, info.Types.Count);
		Assert.Equal(h.ComponentCount, info.Components.Count);
		Assert.Equal(h.TaskCount, info.Tasks.Count);
		Assert.Equal(h.IconCount, info.Icons.Count);
		Assert.Equal(h.IniEntryCount, info.IniEntries.Count);
		Assert.Equal(h.RegistryEntryCount, info.RegistryEntries.Count);
		Assert.Equal(h.DeleteEntryCount, info.DeleteEntries.Count);
		Assert.Equal(h.UninstallDeleteEntryCount, info.UninstallDeleteEntries.Count);
		Assert.Equal(h.RunEntryCount, info.RunEntries.Count);
		Assert.Equal(h.UninstallRunEntryCount, info.UninstallRunEntries.Count);
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void ScriptEntriesHaveSaneContent(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var info = archive.Info;

		Assert.All(info.RegistryEntries,
			e => Assert.False(string.IsNullOrEmpty(e.Key), "注册表条目 key 不应为空"));
		Assert.All(info.RunEntries,
			e => Assert.False(string.IsNullOrEmpty(e.Name), "运行条目 name 不应为空"));
		Assert.All(info.Icons,
			e => Assert.False(string.IsNullOrEmpty(e.Name), "图标条目 name 不应为空"));
		Assert.All(info.Components,
			e => Assert.False(string.IsNullOrEmpty(e.Name), "组件条目 name 不应为空"));
	}
}
