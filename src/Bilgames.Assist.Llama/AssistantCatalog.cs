namespace Bilgames.Assist.Llama;

/// <summary>A file the assistant downloads, with its published checksum.</summary>
public sealed record DownloadSpec(string Label, string Url, string FileName, long Size, string Sha256);

/// <summary>
/// The pinned model and llama.cpp build. Both were tested together; updating either means
/// changing these values (sizes and SHA-256s come from Hugging Face's LFS metadata and GitHub's
/// release digests) and testing again.
/// </summary>
public static class AssistantCatalog
{
    /// <summary>Qwen3.5-35B-A3B (Apache-2.0): a 35-billion-parameter mixture-of-experts model with about 3 billion active per token.</summary>
    public static readonly DownloadSpec Model = new(
        "Qwen3.5-35B-A3B (Q4_K_M)",
        "https://huggingface.co/unsloth/Qwen3.5-35B-A3B-GGUF/resolve/main/Qwen3.5-35B-A3B-Q4_K_M.gguf",
        "Qwen3.5-35B-A3B-Q4_K_M.gguf",
        22_016_023_168,
        "3b46d1066bc91cc2d613e3bc22ce691dd77e6f0d33c9060690d24ce6de494375");

    /// <summary>llama.cpp's server, Vulkan build: runs on NVIDIA, AMD and Intel graphics, and on the CPU.</summary>
    public static readonly DownloadSpec Runtime = new(
        "llama.cpp b11819 (Vulkan)",
        "https://github.com/ggml-org/llama.cpp/releases/download/b11819/llama-b11819-bin-win-vulkan-x64.zip",
        "llama-b11819-bin-win-vulkan-x64.zip",
        36_000_000, // approximate; the checksum is what's checked
        "c3b2484d18258425fe3f69d0b6d07dce01501d6e010578dad3e53150a30df557");

    public const string RuntimeBuild = "b11819";

    /// <summary>
    /// Memory the model needs across graphics memory and RAM (weights plus working space). Below this
    /// the app still lets you try, but warns that it may not fit.
    /// </summary>
    public const long RecommendedMemoryBytes = 26L * 1024 * 1024 * 1024;
}
