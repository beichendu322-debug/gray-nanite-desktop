using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StellarisUI;

public sealed class AppConfig
{
	public int ConfigVersion { get; set; }

	public List<FolderBinding> Folders { get; set; } = new List<FolderBinding>();

	public bool StartWithWindows { get; set; }

	public string? CodexSessionId { get; set; }

	public string? StorageDrive { get; set; }

	public bool QQMusicAutoStart { get; set; }

	public bool QQMusicAutoPlay { get; set; }

	public string? QQMusicPlaylistLink { get; set; }

	public string? QQMusicPlaylistName { get; set; }

	public double QQMusicVolume { get; set; }

	public static AppConfig CreateDefault(string desktop)
	{
		(string, string, string, string)[] definitions = new(string, string, string, string)[8]
		{
			("games", "01", "战术模拟库", "游戏"),
			("media", "02", "全息媒体库", "多媒体"),
			("ai", "03", "智械研究部", "人工智能相关"),
			("social", "04", "外交通讯站", "社交"),
			("tools", "05", "工程工具舱", "工具"),
			("history", "06", "历史数据库", "历史资料"),
			("grad", "07", "学术研究区", "研究生阶段"),
			("inbox", "IN", "未分类信号", "待整理")
		};
		return new AppConfig
		{
			ConfigVersion = 11,
			StartWithWindows = true,
			QQMusicAutoStart = true,
			QQMusicAutoPlay = true,
			QQMusicPlaylistName = "上次播放队列",
			QQMusicVolume = 62.0,
			Folders = definitions.Select(((string, string, string, string) d) => new FolderBinding
			{
				Id = d.Item1,
				Code = d.Item2,
				TerminalName = d.Item3,
				SourceName = d.Item4,
				Path = Path.Combine(desktop, d.Item4)
			}).ToList()
		};
	}
}
