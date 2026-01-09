using Microsoft.Extensions.VectorData;

namespace SourceChat.Infrastructure.Storage;

public sealed class VectorRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    [VectorStoreVector(Dimensions: 4096)]
    public ReadOnlyMemory<float> Vector { get; set; }
}