using System;
using System.Collections.Generic;
using System.Linq;

namespace FFXIV_Craft_Architect.Core.Models;

/// <summary>
/// Represents a target item in a crafting project.
/// Shared between WPF and Web applications.
/// </summary>
public class ProjectItem
{
    /// <summary>
    /// The item ID from Garland Tools.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The item icon ID.
    /// </summary>
    public int IconId { get; set; }

    /// <summary>
    /// Quantity needed.
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Whether HQ is required.
    /// </summary>
    public bool MustBeHq { get; set; }

    /// <summary>
    /// Legacy property for WPF compatibility.
    /// </summary>
    public bool IsHqRequired
    {
        get => MustBeHq;
        set => MustBeHq = value;
    }

    public override string ToString() => $"{Name} x{Quantity}";
}
