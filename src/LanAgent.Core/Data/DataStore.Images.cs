using System.Security.Cryptography;
using System.Text.Json;
using LanAgent.Core.Models;

namespace LanAgent.Core.Data;

public partial class DataStore
{
    public void RemoveRejectedImage(Guid conversationId, ImageAttachment image)
    {
        _lock.EnterWriteLock();
        try
        {
            using var transaction = BeginTransaction();
            var conversation = _data.Conversations.Single(c => c.Id == conversationId);
            var affected = conversation.Messages.Where(m => m.Images.Contains(image)).ToArray();
            if (affected.Length == 0) return;
            var directory = Path.Combine(Path.GetDirectoryName(_filePath)!, "rejected-images");
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(image, JsonOptions);
            var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + ".json");
            if (!File.Exists(path)) WriteToFileAtomic(bytes, path);
            foreach (var message in affected)
            {
                message.Images.RemoveAll(item => item == image);
                message.Content += $"\n[附件 {image.Name} 被图片服务拒绝，已从模型上下文移除；原始附件已备份。]";
                message.ProviderStateJson = null;
            }
            SaveAndVerify(conversationId);
        }
        finally { _lock.ExitWriteLock(); }
    }
}
