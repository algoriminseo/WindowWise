using System;

namespace WindowWise.Models
{
    public sealed class SharedFileInfo
    {
        public Guid Id { get; init; }
        public string Name { get; init; }
        public long Size { get; init; }

        public SharedFileInfo(Guid Id, string Name, long Size)
        {
            this.Id = Id;
            this.Name = Name;
            this.Size = Size;
        }
    }
}
