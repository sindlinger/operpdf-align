using System;

namespace Obj.Models
{
    public class ModificationArea
    {
        public int PageNumber { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public int ObjectNumber { get; set; }
        public int Generation { get; set; }
    }
}
