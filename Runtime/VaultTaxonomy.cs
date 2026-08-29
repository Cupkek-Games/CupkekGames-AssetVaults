using System;
using System.Collections.Generic;
using System.IO;

namespace CupkekGames.AssetVaults
{
  /// <summary>
  /// What a pack primarily <b>is</b>. Exactly one per pack, from this closed
  /// list, because its job is to group the list - and a grouping field that can
  /// hold two answers stops grouping.
  ///
  /// <para>The axis is "what is this thing in a scene", not "what file types are
  /// in it". `3d` is not a category: KayKit, ToonScapes and Vefects are all 3D
  /// and belong in different places. What each pack is <i>made of</i> is a tag,
  /// and a derived one at that - see <see cref="VaultContent"/>.</para>
  ///
  /// <para>Deliberately coarse. Splitting audio into music/voice/ambience, or
  /// creatures out of characters, gives fifteen groups for nineteen packs, at
  /// which point the grouping is just the list again with extra headers.</para>
  /// </summary>
  public static class VaultCategory
  {
    /// <summary>Music, voice, ambience, SFX, foley.</summary>
    public const string Audio = "audio";

    /// <summary>Rigged humanoids, NPCs, creatures, monsters.</summary>
    public const string Characters = "characters";

    /// <summary>Props, buildings, foliage, terrain, skyboxes.</summary>
    public const string Environment = "environment";

    /// <summary>Clips and controllers that ship no models of their own.</summary>
    public const string Animation = "animation";

    /// <summary>Particles, trails, effect shaders.</summary>
    public const string Vfx = "vfx";

    /// <summary>Icons, sprites, fonts, screen art.</summary>
    public const string Ui = "ui";

    /// <summary>Tiling textures and materials with no meshes attached.</summary>
    public const string Surfaces = "surfaces";

    /// <summary>Concept art, marketing, scratch - things the game never loads.</summary>
    public const string Reference = "reference";

    /// <summary>Display order too: the list is grouped in this sequence.</summary>
    public static readonly string[] All =
    {
      Audio, Characters, Environment, Animation, Vfx, Ui, Surfaces, Reference,
    };

    public const string Uncategorised = "";

    public static bool IsKnown(string category)
    {
      if (string.IsNullOrEmpty(category)) return false;
      foreach (string known in All)
      {
        if (string.Equals(known, category, StringComparison.OrdinalIgnoreCase)) return true;
      }

      return false;
    }

    public static string Label(string category)
    {
      switch (category)
      {
        case Audio: return "Audio";
        case Characters: return "Characters";
        case Environment: return "Environment";
        case Animation: return "Animation";
        case Vfx: return "VFX";
        case Ui: return "UI";
        case Surfaces: return "Surfaces";
        case Reference: return "Reference";
        default: return "Uncategorised";
      }
    }

    /// <summary>
    /// Where a pack sorts. Uncategorised last, so filing something is rewarded
    /// by it leaving the bottom of the window.
    /// </summary>
    public static int Order(string category)
    {
      for (int i = 0; i < All.Length; i++)
      {
        if (string.Equals(All[i], category, StringComparison.OrdinalIgnoreCase)) return i;
      }

      return All.Length;
    }
  }

  /// <summary>
  /// The typed tags: where a pack came from, which no scan can work out.
  ///
  /// <para>A closed vocabulary on purpose. Free-text tags become `sfx`, `SFX`
  /// and `sound-effects` within a month, and three spellings of one tag is worse
  /// than no tags. The single exception is <see cref="VendorPrefix"/>, because
  /// "which Synty packs do I own" is a real question and no fixed list can carry
  /// every vendor name.</para>
  /// </summary>
  public static class VaultTag
  {
    public const string Store = "store";
    public const string OwnAuthored = "own-authored";
    public const string Free = "free";
    public const string Paid = "paid";

    /// <summary>Anything after this prefix is free text: `vendor:synty`.</summary>
    public const string VendorPrefix = "vendor:";

    public static readonly string[] Provenance = { Store, OwnAuthored, Free, Paid };

    public static bool IsVendor(string tag)
      => !string.IsNullOrEmpty(tag)
         && tag.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A tag is allowed if it is in the vocabulary, is a vendor tag, or is a
    /// derived content tag - the last so that a manifest written by an older
    /// version does not have its tags silently dropped.
    /// </summary>
    public static bool IsKnown(string tag)
    {
      if (string.IsNullOrWhiteSpace(tag)) return false;
      if (IsVendor(tag)) return true;
      foreach (string known in Provenance)
      {
        if (string.Equals(known, tag, StringComparison.OrdinalIgnoreCase)) return true;
      }

      return VaultContent.IsContentTag(tag);
    }

    public static string Normalise(string tag)
      => string.IsNullOrWhiteSpace(tag) ? string.Empty : tag.Trim().ToLowerInvariant();
  }

  /// <summary>
  /// What a pack is made of, counted rather than typed.
  ///
  /// <para>These are exactly the tags a scanner can be trusted with, so nobody
  /// types them and they cannot go stale: the census runs in the same directory
  /// walk that already counts files and bytes, and a pack that gains a hundred
  /// textures says so the next time the window reloads.</para>
  ///
  /// <para>Two obvious tags are missing on purpose. <c>sprites</c> is not
  /// detectable - a <c>.png</c> is a texture or a sprite depending on its
  /// importer, which lives in the <c>.meta</c>. <c>rigs</c> is not detectable
  /// either: a rig is inside an FBX, not beside it. Guessing at both would put
  /// wrong tags on packs, which is worse than no tag.</para>
  /// </summary>
  public static class VaultContent
  {
    public const string Models = "models";
    public const string Textures = "textures";
    public const string Materials = "materials";
    public const string Shaders = "shaders";
    public const string Animations = "animations";
    public const string Prefabs = "prefabs";
    public const string Scenes = "scenes";
    public const string Audio = "audio";
    public const string Fonts = "fonts";
    public const string Scripts = "scripts";
    public const string VfxGraphs = "vfx-graphs";

    public static readonly string[] All =
    {
      Models, Textures, Materials, Shaders, Animations, Prefabs, Scenes, Audio,
      Fonts, Scripts, VfxGraphs,
    };

    /// <summary>
    /// A kind has to be 5% of the pack to be worth saying. Three stray PNGs in
    /// an audio pack do not make it a texture pack, and a tag that is true of
    /// everything sorts nothing.
    /// </summary>
    private const float ShareThreshold = 0.05f;

    /// <summary>
    /// ...or fifty files of it, whatever the share. A share alone was measured
    /// against the real vault and got the important case wrong: Vefects holds 68
    /// VFX graphs among 1554 files, which is 4.4%, so the one tag that pack
    /// exists for fell under the bar. Fifty files of a kind is a body of work no
    /// matter what it is diluted by.
    /// </summary>
    private const int CountFloor = 50;

    public static bool IsContentTag(string tag)
    {
      foreach (string known in All)
      {
        if (string.Equals(known, tag, StringComparison.OrdinalIgnoreCase)) return true;
      }

      return false;
    }

    /// <summary>
    /// Which kind an extension belongs to, or null. Lowercase, with the dot.
    /// </summary>
    public static string Kind(string extension)
    {
      switch (extension)
      {
        case ".fbx":
        case ".obj":
        case ".blend":
        case ".dae":
        case ".3ds":
        case ".gltf":
        case ".glb":
          return Models;

        case ".png":
        case ".tga":
        case ".jpg":
        case ".jpeg":
        case ".psd":
        case ".exr":
        case ".tif":
        case ".tiff":
        case ".hdr":
        case ".bmp":
          return Textures;

        case ".mat":
          return Materials;

        case ".shader":
        case ".shadergraph":
        case ".shadersubgraph":
        case ".cginc":
        case ".hlsl":
        case ".compute":
          return Shaders;

        case ".anim":
        case ".controller":
        case ".overridecontroller":
          return Animations;

        case ".prefab":
          return Prefabs;

        case ".unity":
          return Scenes;

        case ".wav":
        case ".ogg":
        case ".mp3":
        case ".aiff":
        case ".aif":
        case ".flac":
          return Audio;

        case ".ttf":
        case ".otf":
          return Fonts;

        case ".cs":
          return Scripts;

        case ".vfx":
          return VfxGraphs;

        default:
          return null;
      }
    }

    /// <summary>
    /// Turn a census into tags.
    ///
    /// <para><paramref name="total"/> excludes <c>.meta</c> files, which are
    /// roughly half of every pack and would halve every share.</para>
    ///
    /// <para><see cref="Scripts"/> ignores the threshold: two C# files in a
    /// five-thousand-file pack is 0.04% and still the difference between a
    /// fresh clone compiling and not.</para>
    /// </summary>
    public static List<string> Tags(Dictionary<string, int> census, int total)
    {
      var tags = new List<string>();
      if (census == null || total <= 0) return tags;

      foreach (string kind in All)
      {
        if (!census.TryGetValue(kind, out int count) || count == 0) continue;
        if (kind == Scripts || count >= CountFloor || count >= total * ShareThreshold)
        {
          tags.Add(kind);
        }
      }

      return tags;
    }

    /// <summary>
    /// The census on its own, for callers that are not already walking the
    /// folder. <see cref="VaultStatus.Inspect"/> does its own counting inside
    /// the walk it was doing anyway.
    /// </summary>
    public static List<string> Describe(string dir)
    {
      if (!Directory.Exists(dir)) return new List<string>();

      var census = new Dictionary<string, int>();
      int total = 0;
      foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
      {
        total += Count(file, census) ? 1 : 0;
      }

      return Tags(census, total);
    }

    /// <summary>
    /// Add one file to a census. Returns false for <c>.meta</c>, which must not
    /// count toward the total.
    /// </summary>
    public static bool Count(string file, Dictionary<string, int> census)
    {
      string extension = Path.GetExtension(file).ToLowerInvariant();
      if (extension == ".meta") return false;

      string kind = Kind(extension);
      if (kind != null)
      {
        census.TryGetValue(kind, out int count);
        census[kind] = count + 1;
      }

      return true;
    }
  }
}
