namespace FFM.App.Services;

public static class PortraitPromptBuilder
{
    public static string Build(string? hairstyle, string? facialHair, int? playerAge, int? targetAge)
    {
        var changes = new List<string>();
        var hairUnspecified = string.IsNullOrWhiteSpace(hairstyle) || hairstyle.StartsWith("Keep", StringComparison.OrdinalIgnoreCase);
        var beardUnspecified = string.IsNullOrWhiteSpace(facialHair) || facialHair.StartsWith("Keep", StringComparison.OrdinalIgnoreCase);
        if (!hairUnspecified) changes.Add(HairstyleInstruction(hairstyle!));
        if (!beardUnspecified) changes.Add(FacialHairInstruction(facialHair!));
        if (playerAge is int age && targetAge is int desired && desired > age)
        {
            var years = desired - age;
            changes.Add($"Age the person's appearance to approximately {desired} years old, keeping the same identity. {AgeVariationInstruction(years, hairUnspecified, beardUnspecified)}");
        }
        var requested = changes.Count == 0 ? "No change selected." : "Make exactly these requested changes: " + string.Join(" ", changes);
        return "Edit the attached original portrait rather than creating a new person. " + requested +
               " Preserve the same person's identity, facial structure, pose, expression, clothing, crop, lighting, background and aspect ratio. Return a PNG. Preserve real alpha transparency where present; do not flatten it onto black, white, a colored matte or a checkerboard. Keep clean natural hair and skin edges with smooth antialiasing: no outline, stroke, black halo, colored fringe, isolated bright/red edge pixels or oversharpened border. Do not add text, watermark, frame, padding or collage.";
    }

    private static string HairstyleInstruction(string style) => style switch
    {
        "Buzz cut" => "Change the hairstyle to an even close buzz cut with clean natural edges.",
        "Crew cut" => "Change the hairstyle to a short crew cut with a slightly longer top and softly tapered sides.",
        "Textured crop" => "Change the hairstyle to a short textured crop with a natural forward fringe and tapered sides.",
        "Undercut" => "Change the hairstyle to an undercut with longer hair on top and neatly shorter sides.",
        "Slicked back" => "Change the hairstyle to medium-length hair swept back naturally, without a wet or exaggerated shine.",
        "Long tied back" => "Change the hairstyle to longer hair tied back neatly, preserving a realistic hairline and natural texture.",
        _ => $"Change the hairstyle to a natural {style.ToLowerInvariant()}, preserving plausible hair color and texture."
    };

    private static string FacialHairInstruction(string style) => style switch
    {
        "Light stubble" => "Add neat, very short light stubble across the natural beard area.",
        "Heavy stubble" => "Add even heavy stubble with clearly visible short beard growth.",
        "Short beard" => "Add a neatly trimmed short beard with natural cheek and neckline edges.",
        "Full beard" => "Add a full, well-groomed beard with natural density and edges.",
        "Goatee" => "Add a tidy goatee around the chin, with the cheeks clean-shaven.",
        "Circle beard" => "Add a connected moustache and chin beard forming a neat circle beard.",
        "Van Dyke" => "Add a distinct moustache and pointed chin beard with clean-shaven cheeks.",
        "Balbo" => "Add a shaped chin beard with a separate moustache and clean-shaven cheeks.",
        _ => $"Change the facial hair to a natural {style.ToLowerInvariant()} with realistic density and edges."
    };

    private static string AgeVariationInstruction(int years, bool hairUnspecified, bool beardUnspecified)
    {
        var scale = years <= 3
            ? "Keep age-related changes barely noticeable."
            : years <= 10
                ? "Use modest age-appropriate changes in skin texture and greying."
                : "Show clear but natural age progression with suitable skin texture, some thinning or recession, and grey at the temples where plausible.";
        var hair = hairUnspecified
            ? "Allow only a subtle natural variation of the same hairstyle; for larger age gaps, mild thinning, recession or temple grey may fit."
            : "The selected hairstyle takes precedence; adjust only its texture and greying to fit the target age.";
        var beard = beardUnspecified
            ? "Vary facial hair slightly only when facial hair is already present, and add grey only when age-appropriate. Never add facial hair to a clean-shaven person."
            : "Keep the selected facial-hair style and adjust only its texture and greying to fit the target age.";
        return $"{scale} {hair} {beard}";
    }
}
