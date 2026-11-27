using System.Collections.Generic;

namespace DbConnect.Models;

public class LabelColorOption
{
    public string Name { get; init; } = string.Empty;
    public string Hex { get; init; } = string.Empty;

    public static readonly IReadOnlyList<LabelColorOption> Defaults =
    [
        new() { Name = "None",   Hex = ""        },
        new() { Name = "Red",    Hex = "#EF5350"  },
        new() { Name = "Orange", Hex = "#FF9800"  },
        new() { Name = "Yellow", Hex = "#FFEB3B"  },
        new() { Name = "Green",  Hex = "#4CAF50"  },
        new() { Name = "Blue",   Hex = "#2196F3"  },
        new() { Name = "Purple", Hex = "#9C27B0"  },
        new() { Name = "Grey",   Hex = "#9E9E9E"  },
    ];
}
