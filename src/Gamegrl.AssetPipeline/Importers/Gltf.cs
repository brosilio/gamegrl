using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Gamegrl.Assets;

namespace Gamegrl.AssetPipeline.Importers;

/// <summary>
/// A glTF 2.0 mesh importer (.gltf with external or data-URI buffers, and .glb). Reads every triangle primitive of
/// every mesh, applying node transforms, into one mesh: POSITION, NORMAL (generated flat if absent), TEXCOORD_0,
/// and indices of any width. Uses JsonDocument, which needs no reflection.
/// TODO(milestone 3+): materials, multiple meshes as separate assets, skinning, morph targets, sparse accessors.
/// </summary>
public static class Gltf
{
    public static MeshData Import(ReadOnlySpan<byte> file, Func<string, byte[]> loadExternal)
    {
        byte[] json;
        byte[]? bin = null;
        if (file.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(file) == 0x46546C67) // "glTF"
        {
            int at = 12;
            json = [];
            while (at + 8 <= file.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
                var chunk = file.Slice(at + 8, length);
                if (type == 0x4E4F534A) json = chunk.ToArray();      // "JSON"
                else if (type == 0x004E4942) bin = chunk.ToArray();  // "BIN\0"
                at += 8 + length;
            }
        }
        else json = file.ToArray();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var buffers = new List<byte[]>();
        if (root.TryGetProperty("buffers", out var bufs))
        {
            foreach (var b in bufs.EnumerateArray())
            {
                if (b.TryGetProperty("uri", out var uri))
                {
                    string u = uri.GetString()!;
                    int comma = u.IndexOf(',');
                    buffers.Add(u.StartsWith("data:", StringComparison.Ordinal) && comma > 0
                        ? Convert.FromBase64String(u[(comma + 1)..])
                        : loadExternal(Uri.UnescapeDataString(u)));
                }
                else buffers.Add(bin ?? throw new FormatException("glTF buffer without a uri needs a GLB BIN chunk."));
            }
        }

        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var meshes = root.TryGetProperty("meshes", out var m) ? m : default;
        if (meshes.ValueKind != JsonValueKind.Array) throw new FormatException("glTF has no meshes.");

        // Walk the default scene's nodes (or every root node) so node transforms apply; fall back to raw meshes.
        bool any = false;
        if (root.TryGetProperty("nodes", out var nodes))
        {
            var roots = new List<int>();
            if (root.TryGetProperty("scenes", out var scenes))
            {
                int s = root.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
                foreach (var n in scenes[s].GetProperty("nodes").EnumerateArray()) roots.Add(n.GetInt32());
            }
            else for (int i = 0; i < nodes.GetArrayLength(); i++) roots.Add(i);
            foreach (int r in roots) any |= WalkNode(root, nodes, r, Matrix4x4.Identity, buffers, meshes, vertices, indices);
        }
        if (!any)
            for (int i = 0; i < meshes.GetArrayLength(); i++) AppendMesh(root, meshes[i], Matrix4x4.Identity, buffers, vertices, indices);

        if (vertices.Count == 0) throw new FormatException("glTF has no triangle geometry.");
        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    private static bool WalkNode(JsonElement root, JsonElement nodes, int index, Matrix4x4 parent, List<byte[]> buffers, JsonElement meshes,
        List<Vertex> vertices, List<uint> indices)
    {
        var node = nodes[index];
        var local = Matrix4x4.Identity;
        if (node.TryGetProperty("matrix", out var mat))
        {
            var f = Floats(mat, 16); // column-major in glTF; System.Numerics is row-major with row vectors: same memory order
            local = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        else
        {
            var t = node.TryGetProperty("translation", out var tr) ? Floats(tr, 3) : [0f, 0, 0];
            var r = node.TryGetProperty("rotation", out var ro) ? Floats(ro, 4) : [0f, 0, 0, 1];
            var s = node.TryGetProperty("scale", out var sc) ? Floats(sc, 3) : [1f, 1, 1];
            local = Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(r[0], r[1], r[2], r[3]))
                * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
        }
        var world = local * parent;
        bool any = false;
        if (node.TryGetProperty("mesh", out var mi))
        {
            AppendMesh(root, meshes[mi.GetInt32()], world, buffers, vertices, indices);
            any = true;
        }
        if (node.TryGetProperty("children", out var children))
            foreach (var c in children.EnumerateArray()) any |= WalkNode(root, nodes, c.GetInt32(), world, buffers, meshes, vertices, indices);
        return any;
    }

    private static void AppendMesh(JsonElement root, JsonElement mesh, Matrix4x4 transform, List<byte[]> buffers, List<Vertex> vertices, List<uint> indices)
    {
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
        {
            int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
            if (mode != 4) continue; // triangles only
            var attrs = prim.GetProperty("attributes");
            var positions = ReadVec(root, buffers, attrs.GetProperty("POSITION").GetInt32(), 3);
            float[]? normals = attrs.TryGetProperty("NORMAL", out var na) ? ReadVec(root, buffers, na.GetInt32(), 3) : null;
            float[]? uvs = attrs.TryGetProperty("TEXCOORD_0", out var ua) ? ReadVec(root, buffers, ua.GetInt32(), 2) : null;
            int count = positions.Length / 3;
            uint[] prims = prim.TryGetProperty("indices", out var ia)
                ? ReadIndices(root, buffers, ia.GetInt32())
                : Enumerable.Range(0, count).Select(i => (uint)i).ToArray();

            uint baseVertex = (uint)vertices.Count;
            for (int i = 0; i < count; i++)
            {
                var p = Vector3.Transform(new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]), transform);
                var n = normals is null ? Vector3.Zero
                    : Vector3.Normalize(Vector3.TransformNormal(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]), normalMatrix));
                var uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i * 2], uvs[i * 2 + 1]);
                vertices.Add(new Vertex(p, n, uv));
            }
            foreach (uint i in prims)
            {
                if (i >= count) throw new FormatException("glTF index out of range.");
                indices.Add(baseVertex + i);
            }
            if (normals is null) FlatNormals(vertices, indices, baseVertex, prims.Length);
        }
    }

    private static void FlatNormals(List<Vertex> vertices, List<uint> indices, uint baseVertex, int count)
    {
        int start = indices.Count - count;
        for (int t = start; t + 2 < indices.Count; t += 3)
        {
            var a = vertices[(int)indices[t]].Position;
            var b = vertices[(int)indices[t + 1]].Position;
            var c = vertices[(int)indices[t + 2]].Position;
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            for (int k = 0; k < 3; k++)
            {
                var v = vertices[(int)indices[t + k]];
                v.Normal += n;
                vertices[(int)indices[t + k]] = v;
            }
        }
        for (int i = (int)baseVertex; i < vertices.Count; i++)
        {
            var v = vertices[i];
            if (v.Normal != Vector3.Zero) v.Normal = Vector3.Normalize(v.Normal);
            vertices[i] = v;
        }
    }

    private static float[] Floats(JsonElement array, int n)
    {
        var f = new float[n];
        int i = 0;
        foreach (var e in array.EnumerateArray())
        {
            if (i == n) break;
            f[i++] = e.GetSingle();
        }
        return f;
    }

    private static (byte[] Data, int Offset, int Stride, int Count, int ComponentType, bool Normalized) Accessor(JsonElement root, List<byte[]> buffers, int index, int elementBytes)
    {
        var acc = root.GetProperty("accessors")[index];
        if (acc.TryGetProperty("sparse", out _)) throw new FormatException("Sparse glTF accessors aren't supported yet.");
        int count = acc.GetProperty("count").GetInt32();
        int componentType = acc.GetProperty("componentType").GetInt32();
        bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
        var view = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
        var data = buffers[view.GetProperty("buffer").GetInt32()];
        int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : elementBytes;
        if (count > 0 && offset + (long)stride * (count - 1) + elementBytes > data.Length) throw new FormatException("glTF accessor runs past its buffer.");
        return (data, offset, stride, count, componentType, normalized);
    }

    private static float[] ReadVec(JsonElement root, List<byte[]> buffers, int index, int n)
    {
        var acc = root.GetProperty("accessors")[index];
        int componentType = acc.GetProperty("componentType").GetInt32();
        int size = componentType switch { 5126 => 4, 5123 or 5122 => 2, _ => 1 };
        var (data, offset, stride, count, ct, normalized) = Accessor(root, buffers, index, size * n);
        var result = new float[count * n];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < n; c++)
            {
                var s = data.AsSpan(offset + i * stride + c * size);
                result[i * n + c] = ct switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
                    5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
                    5121 => normalized ? s[0] / 255f : s[0],
                    5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(s),
                    5120 => normalized ? Math.Max((sbyte)s[0] / 127f, -1) : (sbyte)s[0],
                    _ => throw new FormatException($"Unsupported glTF component type {ct}."),
                };
            }
        return result;
    }

    private static uint[] ReadIndices(JsonElement root, List<byte[]> buffers, int index)
    {
        int componentType = root.GetProperty("accessors")[index].GetProperty("componentType").GetInt32();
        int size = componentType switch { 5125 => 4, 5123 => 2, 5121 => 1, _ => throw new FormatException($"Bad glTF index type {componentType}.") };
        var (data, offset, stride, count, _, _) = Accessor(root, buffers, index, size);
        var result = new uint[count];
        for (int i = 0; i < count; i++)
        {
            var s = data.AsSpan(offset + i * stride);
            result[i] = size switch { 4 => BinaryPrimitives.ReadUInt32LittleEndian(s), 2 => BinaryPrimitives.ReadUInt16LittleEndian(s), _ => s[0] };
        }
        return result;
    }
}
