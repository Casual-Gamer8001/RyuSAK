// Written for RyuSAK. A trimmed version of Ryubing's Ryujinx.Common/Utilities/EmbeddedResources.cs,
// keeping only what the shader translator uses (its GLSL helper functions).
using System;
using System.IO;
using System.Reflection;

namespace Ryujinx.Common
{
    public static class EmbeddedResources
    {
        public static string ReadAllText(string filename)
        {
            string[] segments = filename.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (segments.Length >= 2 && assembly.GetName().Name == segments[0])
                {
                    string manifestUri = segments[0] + "." + segments[1].Replace('/', '.');

                    using Stream stream = assembly.GetManifestResourceStream(manifestUri)
                        ?? throw new FileNotFoundException($"Embedded resource {filename} was not found.");
                    using StreamReader reader = new(stream);

                    return reader.ReadToEnd();
                }
            }

            throw new FileNotFoundException($"Embedded resource {filename} was not found.");
        }
    }
}
