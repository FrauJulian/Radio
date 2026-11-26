using Radiosender.Models;

namespace Radiosender.Controllers;

public record UploadResult(bool Success, string Message, Song? Song = null);
