// ArConfig.cs — Model serializable untuk assets/ar/config.json (sama persis
// dengan schema versi web). Diparse via JsonUtility.

using System;
using System.Collections.Generic;

namespace ArBooth
{
    [Serializable]
    public class Vec3Json
    {
        public float x, y, z;
    }

    [Serializable]
    public class Vec2Json
    {
        public float min, max;
    }

    [Serializable]
    public class ParticleJson
    {
        public string type;
        public int count;
        public float speed;
        public float gravity;
        public Vec2Json size;
        public string[] colors;
        public float lifetime;
        public float spread;
        public float direction;
        public string src;
        public float sway;
    }

    [Serializable]
    public class AnimationJson
    {
        public string type;
        public float speed;
        public string clipName;
        public int cols;
        public int rows;
        public int frameCount;
        public float fps;
        public ParticleJson particle;
    }

    [Serializable]
    public class TriggerJson
    {
        public string expression;
        public string action;
        public string target;
        public float threshold;
        public float boost;
    }

    [Serializable]
    public class BrandingJson
    {
        public string name;
        public string logo;
        public string logoAlt;
        public string watermark;
    }

    /// <summary>Satu entry item di config.json — runtime state menempel di Runtime.</summary>
    [Serializable]
    public class ArItemConfig
    {
        public string id;
        public string name;
        public string type;      // "2d" | "3d" | "particle"
        public string category;  // slot eksklusif (opsional)
        public string src;
        public string thumbnail;
        public string anchor;
        public Vec3Json offset;
        public float scale = 1f;
        public Vec3Json rotation;
        public AnimationJson animation;  // field mentah — dinormalisasi ke Anim
        public TriggerJson trigger;      // field mentah — dinormalisasi ke Trigger

        // --- Hasil parse/normalisasi (tidak dari JSON) ---
        [NonSerialized] public AnimConfig Anim;
        [NonSerialized] public TriggerConfig Trigger;
        [NonSerialized] public ItemRuntime Runtime = new ItemRuntime();
    }

    [Serializable]
    public class BoothConfig
    {
        public BrandingJson branding;
        public List<ArItemConfig> items;
    }

    /// <summary>animation.* hasil normalisasi (default terisi, tipe tervalidasi).</summary>
    public class AnimConfig
    {
        public string Type = "loop";
        public float Speed = 1f;
        public string ClipName;
        public int Cols, Rows, FrameCount;
        public float Fps = 12f;
        public ParticleConfig Particle;
    }

    /// <summary>trigger.* hasil normalisasi.</summary>
    public class TriggerConfig
    {
        public string Expression;
        public string Action;
        public string Target;
        public float Threshold = 0.5f;
        public float Boost = 3f;
    }

    /// <summary>State animasi runtime yang dimutasi trigger ekspresi — dibaca renderer tiap frame.</summary>
    public class ItemRuntime
    {
        public bool Playing = true;
        public float SpeedMul = 1f;
        public float PlayOnceAt = -1f;
        public float BurstAt = -1f;
        public float BounceAt = -1f;
        public bool ParticleOn;
        public bool Hidden;
    }
}
