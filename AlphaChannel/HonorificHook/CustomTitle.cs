using System.Numerics;
using System.Text.Json.Serialization;
using System.ComponentModel;

namespace AlphaChannel;

public class TitleData
{
	public string? Title = string.Empty;
	public bool IsPrefix;
	public bool IsOriginal;
	public Vector3? Color;
	public Vector3? Glow;

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
	public string? Title = string.Empty;
	public bool IsPrefix;
	public bool IsOriginal;
	public string UniqueId = string.Empty;

	public bool Enabled;
	public TitleConditionType TitleCondition = TitleConditionType.None;
	public int ConditionParam0;

	public Vector3? Color;
	public Vector3? Glow;

	[JsonIgnore] public string DisplayTitle => $"《{Title}》";
}

public enum TitleConditionType
{
	None,

	[Description("Class / Job")]
	ClassJob,

	[Description("Role")]
	JobRole,

	[Description("Gear Set")]
	GearSet,

	[Description("Original Title")]
	Title,
}