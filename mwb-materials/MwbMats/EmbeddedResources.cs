using System.IO;
using System.Reflection;

namespace mwb_materials.MwbMats
{
    static class EmbeddedResources
    {
        public static byte[] Read(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("Embedded resource not found: " + name);
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }
    }
}
