using System;
using System.Collections.Generic;
using System.Text;

namespace WindowWise.Models { 
/// <summary>
///    represents the type of content stored in the clipboard.
///    Guid Id : a uniuqe identifier for the clipboard
///    Content : the actual content copied
///    ContentType : the type of content (text or link) later will be expannded to img, png, etc
///    CopiedAt : use to distinguish the order of the copied content
/// </summary>
    public enum ClipboardType
    {
        Text,
        Link
    }

    public sealed class ClibboardInfo
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public required string Content { get; init; }
        public ClipboardType ContentType { get; init; }
        public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.Now;
    }
}
