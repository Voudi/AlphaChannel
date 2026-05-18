using System.Numerics;
using System.Text.Json.Serialization;

namespace AlphaChannel;

public class TitleData
{
	public string? Title { get; set; } = string.Empty;
	public bool IsPrefix { get; set; }
	public bool IsOriginal { get; set; }
	public Vector3? Color { get; set; }
	public Vector3? Glow { get; set; }

	public static implicit operator TitleData(CustomTitle title) => new()
	{
		Title = title.Title,
		IsPrefix = title.IsPrefix,
		Color = title.Color,
		Glow = title.Glow,
		IsOriginal = title.IsOriginal
	};
	public static implicit operator CustomTitle(TitleData data) => new()
	{
		Title = data.Title,
		IsPrefix = data.IsPrefix,
		Color = data.Color,
		Glow = data.Glow,
		IsOriginal = data.IsOriginal,
	};
}

public class CustomTitle
{
	public string? Title { get; set; } = string.Empty;
	public bool IsPrefix { get; set; }
	public bool IsOriginal { get; set; }
	public string UniqueId { get; set; } = string.Empty;

	public bool Enabled { get; set; }
	public TitleConditionType TitleCondition { get; set; } = TitleConditionType.None;
	public int ConditionParam0 { get; set; }

	public Vector3? Color { get; set; }
	public Vector3? Glow { get; set; }

	[JsonIgnore] public string DisplayTitle => $"《{Title}》";
}

public enum TitleConditionType
{
	None,

	[System.ComponentModel.Description("Class / Job")]
	ClassJob,

	[System.ComponentModel.Description("Role")]
	JobRole,

	[System.ComponentModel.Description("Gear Set")]
	GearSet,

	[System.ComponentModel.Description("Original Title")]
	Title,
}
