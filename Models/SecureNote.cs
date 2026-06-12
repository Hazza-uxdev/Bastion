using System;
using System.Collections.Generic;

namespace SecureVault.Models
{
    public class SecureNote
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Folder { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public bool IsPinned { get; set; } = false;
        public bool IsDeleted { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<NoteSnapshot> History { get; set; } = new();
        public List<NoteAttachment> Attachments { get; set; } = new();
    }

    public class NoteSnapshot
    {
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public string Body { get; set; } = "";
        public string Title { get; set; } = "";
        public string BodyPatch { get; set; } = "";
        public bool IsFullCopy { get; set; } = true;
        public string CompressedBodyBase64 { get; set; } = "";
    }

    public class NoteAttachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string DataBase64 { get; set; } = "";
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }
}
