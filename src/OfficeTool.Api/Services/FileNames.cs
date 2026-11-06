using OfficeTool.Core.Abstractions;

namespace OfficeTool.Api.Services;

/// <summary>
/// 文件名「仅大小写不同」的冲突检测。
///
/// 群晖等 NAS 底层多为 ext4（大小写敏感），<c>Report.docx</c> 与 <c>report.docx</c>
/// 可以并存；而 Windows 客户端经 SMB 映射盘看到的是**同一个**文件名。
/// 结果就是共享盘上有两份内容不同的文件，谁被打开取决于缓存与运气，
/// 而用户在网页列表中会以为自己看到的是重复行。
///
/// 项目 / 检项编码早已按 <c>NOCASE</c> 判重（见 ProjectCatalogService），
/// 文件这一层此前漏了，这里补齐。
///
/// 注意：判定只在**同一目录内**进行，与业务语义一致（不同项目下同名是正常的）。
/// </summary>
internal static class FileNames
{
    /// <summary>目录中是否已存在同名文件（忽略大小写）。</summary>
    public static bool ExistsIgnoringCase(IFileStore store, string directory, string fileName)
    {
        if (store.FileExists(Path.Combine(directory, fileName)))
        {
            return true;
        }

        return store.DirectoryExists(directory)
               && store.ListFiles(directory)
                   .Any(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 供「改名」场景使用：只改大小写时不能算冲突，否则在 Windows 上永远改不动自己的文件名。
    /// </summary>
    public static bool ConflictsWithAnotherFile(IFileStore store, string directory, string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ExistsIgnoringCase(store, directory, newName);
    }
}
