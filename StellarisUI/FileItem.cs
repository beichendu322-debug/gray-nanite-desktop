namespace StellarisUI;

public sealed class FileItem
{
	public string Name { get; set; } = "";

	public string FullPath { get; set; } = "";

	public bool IsDirectory { get; set; }

	public string Badge { get; set; } = "FILE";

	public string TypeLabel { get; set; } = "文件";

	public string ModifiedLabel { get; set; } = "--";
}
