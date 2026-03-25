using System.Collections.Generic;
using UnityEngine;

public static class GrassMeshGenerator
{
    public static Mesh CreateGrassBlade(GrassBladeSettings settings)
    {
        Mesh mesh = new Mesh
        {
            name = "GrassBlade"
        };

        int segmentCount = Mathf.Max(2, settings.segmentCount);
        List<Vector3> vertices = new List<Vector3>(segmentCount * 2);
        List<Vector3> normals = new List<Vector3>(segmentCount * 2);
        List<Vector2> uvs = new List<Vector2>(segmentCount * 2);
        List<int> triangles = new List<int>((segmentCount - 1) * 6);

        for (int i = 0; i < segmentCount; i++)
        {
            float t = i / (float)(segmentCount - 1);
            float height = t * settings.height;
            float width = settings.width * (1f - t * settings.topNarrow);
            float bend = Mathf.Sin(t * Mathf.PI * 0.5f) * settings.bendAmount;

            vertices.Add(new Vector3(-width, height, bend * width));
            vertices.Add(new Vector3(width, height, bend * width));

            normals.Add(Vector3.back);
            normals.Add(Vector3.back);

            if (settings.useVerticalUV)
            {
                uvs.Add(new Vector2(0f, t));
                uvs.Add(new Vector2(1f, t));
            }
            else
            {
                uvs.Add(new Vector2(0f, t * settings.uvScale.y));
                uvs.Add(new Vector2(settings.uvScale.x, t * settings.uvScale.y));
            }
        }

        for (int i = 0; i < segmentCount - 1; i++)
        {
            int baseIndex = i * 2;

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 1);

            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
            triangles.Add(baseIndex + 1);
        }

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        CalculateMeshTangents(mesh);
        mesh.RecalculateBounds();

        return mesh;
    }

    public static void CalculateMeshTangents(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uv = mesh.uv;
        int[] triangles = mesh.triangles;

        Vector4[] tangents = new Vector4[vertices.Length];
        Vector3[] tan1 = new Vector3[vertices.Length];
        Vector3[] tan2 = new Vector3[vertices.Length];

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int i1 = triangles[i];
            int i2 = triangles[i + 1];
            int i3 = triangles[i + 2];

            Vector3 v1 = vertices[i1];
            Vector3 v2 = vertices[i2];
            Vector3 v3 = vertices[i3];

            Vector2 w1 = uv[i1];
            Vector2 w2 = uv[i2];
            Vector2 w3 = uv[i3];

            float x1 = v2.x - v1.x;
            float x2 = v3.x - v1.x;
            float y1 = v2.y - v1.y;
            float y2 = v3.y - v1.y;
            float z1 = v2.z - v1.z;
            float z2 = v3.z - v1.z;

            float s1 = w2.x - w1.x;
            float s2 = w3.x - w1.x;
            float t1 = w2.y - w1.y;
            float t2 = w3.y - w1.y;

            float div = s1 * t2 - s2 * t1;
            float r = Mathf.Approximately(div, 0f) ? 0f : 1f / div;

            Vector3 sdir = new Vector3(
                (t2 * x1 - t1 * x2) * r,
                (t2 * y1 - t1 * y2) * r,
                (t2 * z1 - t1 * z2) * r
            );

            Vector3 tdir = new Vector3(
                (s1 * x2 - s2 * x1) * r,
                (s1 * y2 - s2 * y1) * r,
                (s1 * z2 - s2 * z1) * r
            );

            tan1[i1] += sdir;
            tan1[i2] += sdir;
            tan1[i3] += sdir;

            tan2[i1] += tdir;
            tan2[i2] += tdir;
            tan2[i3] += tdir;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 n = normals[i];
            Vector3 t = tan1[i];

            Vector3.OrthoNormalize(ref n, ref t);
            tangents[i] = new Vector4(
                t.x,
                t.y,
                t.z,
                Vector3.Dot(Vector3.Cross(n, t), tan2[i]) < 0f ? -1f : 1f
            );
        }

        mesh.tangents = tangents;
    }
}
