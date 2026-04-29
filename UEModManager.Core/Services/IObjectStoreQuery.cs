using System.Collections.Generic;

namespace UEModManager.Services
{
    /// <summary>
    /// 对象仓库的"只读契约"。
    /// Domain 服务通过此接口查询包文件物理路径，但不允许直接写入/删除（写操作走具体 ObjectStore）。
    /// </summary>
    public interface IObjectStoreQuery
    {
        /// <summary>仓库根目录绝对路径。</summary>
        string RepositoryRoot { get; }

        /// <summary>获取指定包的存储目录（可能不存在）。</summary>
        string GetPackageDirectory(string packageKey);

        /// <summary>获取指定包文件子目录。</summary>
        string GetPackageFilesDirectory(string packageKey);

        /// <summary>获取指定包的 manifest.json 路径。</summary>
        string GetManifestPath(string packageKey);

        /// <summary>获取指定包的预览图路径（不存在则返回 null）。</summary>
        string? GetPreviewImagePath(string packageKey);

        /// <summary>列出指定包的所有物理文件。</summary>
        List<string> GetPackageFiles(string packageKey);

        /// <summary>包目录是否存在。</summary>
        bool PackageExists(string packageKey);

        /// <summary>仓库内所有 packageKey。</summary>
        List<string> GetAllPackageKeys();

        /// <summary>仓库总占用字节。</summary>
        long GetTotalSize();
    }
}
