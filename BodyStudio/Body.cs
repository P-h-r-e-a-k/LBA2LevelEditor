using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

public sealed record Bone(int Start, int Count, int Pivot, int Parent, byte[] Record);
// LBA1 polygons carry lighting data: Material 7 / 8 (flat) a normal for the whole face, 9 / 10 (Gouraud) one normal per point;
// lower materials are drawn unlit. Colour is the palette index the shade is added to.
// A textured LBA2 polygon: which entry of the body's texture table it uses and one (U, V) pair per point, 8.8 fixed point pixels of the object texture page.
public sealed record FaceTexture(int Handle, int[] UV);
public sealed record Face(int[] Points, int Colour, int DetailTone = -1, int Material = -1, int FaceNormal = -1, int[]? PointNormals = null, FaceTexture? Texture = null);
// A normal of an LBA1 body: a vector (x, y, z) and the "prenormalized range" its lighting is divided by.
public sealed record BodyNormal(int X, int Y, int Z, int Range);
public sealed record BodyLine(int A, int B, int Colour);
public sealed record BodySphere(int Point, int Radius, int Colour);

public sealed class Body
{
    public int Game;
    // Write the body with the game's lighting data (vertex normals, Gouraud polygons) instead of flat unlit polygons. The normals
    // are computed from the geometry at write time, so this stays right when a generator moves points about.
    public bool Lit;
    // LBA2 textured polygons: the texture table (low 16 bits: offset into the page, high 16: repeat mask) and, when set by the caller, the page (RESS.HQR entry 6, 256 x 256)
    public uint[] Textures = [];
    public byte[]? TexturePage;
    // a body that is not animated (LBA2 fixed objects: OBJFIX.HQR); kept as such when written
    public bool Static;
    public byte[] Header = [];
    public List<Vector3> Vertices = [];
    public List<Bone> Bones = [];
    public List<Face> Faces = [];
    // LBA1 lighting: the normals, in the order of the bones they belong to (NormalBone[i] = the bone of normal i).
    public List<BodyNormal> Normals = [];
    public int[] NormalBone = [];
    public List<BodyLine> Lines = [];
    public List<BodySphere> Spheres = [];
    public int Limit => Game == 1 ? 500 : 550;
    public int DrawBufferBytes => Faces.Sum(f=>4+6*f.Points.Length)+(Lines.Count+Spheres.Count)*12;
    static int U(byte[] b, int p) => BitConverter.ToUInt16(b, p);
    static int I(byte[] b, int p) => BitConverter.ToInt32(b, p);
    static void Range(byte[] b, int p, int n)
    {
        if (p < 0 || n < 0 || p > b.Length - n) throw new InvalidDataException("Body section outside file.");
    }
    public static Body Read(byte[] data, int game, bool allowStatic = false)
    {
        if (game != 1 && game != 2) throw new ArgumentOutOfRangeException(nameof(game));
        try { return ReadInternal(data, game, allowStatic); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException)
        { throw new InvalidDataException("Truncated or invalid body.", e); }
    }
    static Body ReadInternal(byte[] b, int game, bool allowStatic)
    {
        var m = new Body { Game = game };
        int p, count, groupOffset, groupCount;
        if (game == 1)
        {
            Range(b, 0, 16);
            if ((U(b, 0) & 2) == 0) throw new InvalidDataException("Choose an animated body template.");
            p = 16 + U(b, 14); Range(b, 0, p + 2);
            m.Header = b[..p]; count = U(b, p); p += 2;
            Range(b, p, count * 6 + 2);
            for (int i = 0; i < count; i++, p += 6) m.Vertices.Add(ReadVector(b, p));
            groupCount = U(b, p); groupOffset = p + 2;
            Range(b, groupOffset, groupCount * 38 + 2);
            for (int i = 0; i < groupCount; i++)
            {
                int q = groupOffset + i * 38;
                if (U(b,q) % 6 != 0 || U(b,q+4) % 6 != 0) throw new InvalidDataException("Misaligned LBA1 bone reference.");
                int parent = BitConverter.ToInt16(b, q + 6);
                if (parent != -1 && parent % 38 != 0) throw new InvalidDataException("Expected an unpatched on-disk LBA1 body.");
                m.Bones.Add(new(U(b,q)/6, U(b,q+2), U(b,q+4)/6, parent == -1 ? -1 : parent/38, b[q..(q+38)]));
            }
            p = groupOffset + groupCount * 38;
            int normals = U(b,p); p += 2; Range(b,p,normals*8+2);
            for (int i = 0; i < normals; i++) m.Normals.Add(new(BitConverter.ToInt16(b,p+i*8),BitConverter.ToInt16(b,p+i*8+2),BitConverter.ToInt16(b,p+i*8+4),U(b,p+i*8+6)));
            // each bone record says how many of the normals are its own (offset 18), in bone order
            var normalBone = new List<int>();
            for (int i = 0; i < m.Bones.Count; i++) for (int k = 0, c = U(m.Bones[i].Record, 18); k < c; k++) normalBone.Add(i);
            m.NormalBone = normalBone.ToArray();
            p += normals*8;
            int polygons = U(b,p); p += 2;
            for (int i = 0; i < polygons; i++)
            {
                Range(b,p,4); int type = b[p], n = b[p+1], col = U(b,p+2); p += 4;
                if (type > 10 || n < 3 || n > 16) throw new InvalidDataException("Unsupported LBA1 polygon.");
                int faceNormal = -1;
                if (type is 7 or 8) { Range(b,p,2); faceNormal = U(b,p); p += 2; }
                Range(b,p,n*(type >= 9 ? 4 : 2));
                int[] ids = new int[n]; int[]? pointNormals = type >= 9 ? new int[n] : null;
                for (int j = 0; j < n; j++) { if (type >= 9) { pointNormals![j] = U(b,p); p += 2; } int reference = U(b,p); if(reference%6!=0) throw new InvalidDataException("Misaligned polygon point."); ids[j] = reference/6; p += 2; }
                m.Faces.Add(new(ids,col & 255,-1,type,faceNormal,pointNormals));
            }
            Range(b,p,2); int lines = U(b,p); p += 2; Range(b,p,lines*8+2);
            for (int i=0;i<lines;i++,p+=8) m.Lines.Add(new(U(b,p+4)/6,U(b,p+6)/6,b[p+1]));
            int spheres=U(b,p);p+=2;Range(b,p,spheres*8);
            for(int i=0;i<spheres;i++,p+=8)m.Spheres.Add(new(U(b,p+6)/6,U(b,p+4),b[p+1]));
        }
        else
        {
            Range(b,0,96); m.Static=(I(b,0)&256)==0; if (m.Static&&!allowStatic) throw new InvalidDataException("Choose an animated body template.");
            m.Header=b[..96]; groupCount=I(b,32);groupOffset=I(b,36);count=I(b,40);p=I(b,44);
            if(count<1||count>550||groupCount<1||groupCount>30)throw new InvalidDataException("Body exceeds engine limits.");
            Range(b,p,count*8);Range(b,groupOffset,groupCount*8);
            for(int i=0;i<count;i++,p+=8)m.Vertices.Add(ReadVector(b,p));
            int start=0;
            for(int i=0;i<groupCount;i++) { int q=groupOffset+i*8,n=U(b,q+4);m.Bones.Add(new(start,n,U(b,q+2),i==0?-1:U(b,q),b[q..(q+8)]));start+=n; }
            p=I(b,68);int end=I(b,76);Range(b,p,end-p);
            while(p<end)
            {
                Range(b,p,8);int type=U(b,p),n=U(b,p+2);p+=8;
                bool quad=(type&32768)!=0,env=(type&16384)!=0,texture=(type&255)>7;
                int stride=env?16:texture?(quad?32:24):12;
                if((type&255)>23)throw new InvalidDataException("Unsupported LBA2 polygon type.");
                Range(b,p,checked(n*stride));
                for(int i=0;i<n;i++,p+=stride)
                {
                    int[] ids=new int[quad?4:3];for(int j=0;j<ids.Length;j++)ids[j]=U(b,p+j*2);
                    FaceTexture? tex=null;
                    if(texture&&!env){int handle=U(b,p+(quad?28:6));int[] uv=new int[ids.Length*2];for(int j=0;j<uv.Length;j++)uv[j]=U(b,p+12+j*2);tex=new(handle,uv);}
                    m.Faces.Add(new(ids,U(b,p+8)&255,-1,type&255,-1,null,tex));
                }
                if(p>end)throw new InvalidDataException("Polygon block overlaps line section.");
            }
            int lines=I(b,72);p=I(b,76);Range(b,p,checked(lines*8));
            for(int i=0;i<lines;i++,p+=8)m.Lines.Add(new(U(b,p+4),U(b,p+6),U(b,p+2)&255));
            int spheres=I(b,80);p=I(b,84);Range(b,p,checked(spheres*8));
            for(int i=0;i<spheres;i++,p+=8)m.Spheres.Add(new(U(b,p+4),U(b,p+6),U(b,p+2)&255));
            int textureCount=I(b,88),textureOffset=I(b,92);
            if(textureCount>0&&textureCount<4096&&textureOffset>=0&&textureOffset+textureCount*4<=b.Length){m.Textures=new uint[textureCount];for(int i=0;i<textureCount;i++)m.Textures[i]=BitConverter.ToUInt32(b,textureOffset+i*4);}
        }
        m.Validate(strict: false);return m;
    }
    static Vector3 ReadVector(byte[] b,int p)=>new(BitConverter.ToInt16(b,p),BitConverter.ToInt16(b,p+2),BitConverter.ToInt16(b,p+4));
    // strict=false skips the Faces+Lines+Spheres>Limit check: that's the STORED total, not the native
    // renderer's own real constraint -- its shared per-frame primitive sort list only needs to fit whichever
    // polygons are actually visible after backface culling at once (lines/spheres are never culled, but
    // polygons routinely are, often close to half). A real, confirmed case: LBA2 BODY.HQR entry 175 ("Twinsen
    // and Zoe with the umbrella down", a two-character cutscene body) stores 564 primitives -- over the 550
    // cap -- and renders correctly in the real game, because its per-frame visible count never reaches 550
    // from any normal camera angle. The native sort-list fill (AFF_OBJ.CPP) now clamps defensively regardless
    // (drops any primitive beyond the cap rather than overflowing), so reading an EXISTING, already-shipped
    // archive body (Body.Read, strict:false) can trust the real game already renders it fine and shouldn't
    // reject it on the stored total alone. Authoring a NEW body (strict, the default -- every Write() call
    // and every BodyStudio generator) keeps the conservative stored-total limit: there's no way to know in
    // advance how a hand-authored/generated shape's own primitives will distribute across camera angles, so a
    // margin against ever needing more than Limit simultaneously visible is the safer default there.
    public void Validate(bool strict = true)
    {
        if(Vertices.Count<1||Vertices.Count>Limit||Bones.Count<1||Bones.Count>30)
            throw new InvalidDataException("Body exceeds classic engine point or bone limits.");
        if(strict&&Faces.Count+Lines.Count+Spheres.Count>Limit)
            throw new InvalidDataException("Body exceeds classic engine point, primitive, or bone limits.");
        if(Game==1&&DrawBufferBytes>10000)throw new InvalidDataException("Body exceeds LBA1's 10,000-byte projected entity buffer.");
        int next=0;
        for(int i=0;i<Bones.Count;i++)
        {
            var b=Bones[i];
            if(b.Start!=next||b.Count<0||b.Start+b.Count>Vertices.Count||b.Parent>=i||b.Parent< -1||(i>0&&(b.Parent<0||b.Pivot<0||b.Pivot>=b.Start)))
                throw new InvalidDataException("Invalid bone hierarchy or point ownership.");
            next+=b.Count;
        }
        if(next!=Vertices.Count)throw new InvalidDataException("Bone point counts do not cover the mesh.");
        foreach(var f in Faces)if(f.Points.Length<3||f.Points.Any(p=>p<0||p>=Vertices.Count))throw new InvalidDataException("Invalid polygon point.");
        foreach(var l in Lines)if(l.A<0||l.B<0||l.A>=Vertices.Count||l.B>=Vertices.Count)throw new InvalidDataException("Invalid line point.");
        foreach(var s in Spheres)if(s.Point<0||s.Point>=Vertices.Count||s.Radius<0||s.Radius>32767)throw new InvalidDataException("Invalid sphere.");
        foreach(var v in Vertices)if(!float.IsFinite(v.X+v.Y+v.Z)||Math.Abs(v.X)>32767||Math.Abs(v.Y)>32767||Math.Abs(v.Z)>32767)throw new InvalidDataException("Vertex outside signed 16-bit range.");
    }
    // Neutral pose: bone coordinates relative to their already resolved pivot point.
    public Vector3[] World()
    {
        var result=new Vector3[Vertices.Count];
        foreach(var bone in Bones)
        {
            var origin=bone.Parent<0?Vector3.Zero:result[bone.Pivot];
            for(int i=bone.Start;i<bone.Start+bone.Count;i++)result[i]=Vertices[i]+origin;
        }
        return result;
    }
    public void SetWorld(Vector3[] world)
    {
        foreach(var bone in Bones)
        {
            var origin=bone.Parent<0?Vector3.Zero:world[bone.Pivot];
            for(int i=bone.Start;i<bone.Start+bone.Count;i++)Vertices[i]=world[i]-origin;
        }
    }
    // Smooth vertex normals of the neutral pose (unit length): each polygon's area-weighted normal is shared by its points. The game's
    // bodies wind their polygons so that this (Newell) normal points out of the surface (checked on both games' bodies), so no flipping.
    public Vector3[] VertexNormals()
    {
        var world=World();var sum=new Vector3[Vertices.Count];
        foreach(var f in Faces)
        {
            var n=Vector3.Zero;
            for(int i=0;i<f.Points.Length;i++)
            {
                var a=world[f.Points[i]];var b=world[f.Points[(i+1)%f.Points.Length]];
                n+=new Vector3((a.Y-b.Y)*(a.Z+b.Z),(a.Z-b.Z)*(a.X+b.X),(a.X-b.X)*(a.Y+b.Y));
            }
            foreach(int p in f.Points)sum[p]+=n;
        }
        for(int i=0;i<sum.Length;i++)sum[i]=sum[i].LengthSquared()>1e-9f?Vector3.Normalize(sum[i]):Vector3.Zero;
        return sum;
    }
    // LBA2 vertex normals are 10240 long, LBA1 normals 63 long with a "range" of 315 (what the retail bodies use).
    static short Scaled(float v,float length)=>(short)Math.Clamp((int)Math.Round(v*length),-32767,32767);
    public byte[] Write()
    {
        Validate();using var s=new MemoryStream();using var w=new BinaryWriter(s);
        var world=World();
        var normalsOfPoints=Lit?VertexNormals():null;
        if(Game==1)
        {
            byte[] header=(byte[])Header.Clone();
            for(int axis=0;axis<3;axis++) { short lo=Checked(world.Min(v=>Component(v,axis))),hi=Checked(world.Max(v=>Component(v,axis)));BitConverter.GetBytes(lo).CopyTo(header,2+axis*4);BitConverter.GetBytes(hi).CopyTo(header,4+axis*4); }
            w.Write(header);w.Write((ushort)Vertices.Count);foreach(var v in Vertices)WriteVector(w,v);
            w.Write((ushort)Bones.Count);
            // Start/Pivot/Parent (offsets 0/4/6, byte offsets -- Read's own U(b,q)/6, U(b,q+4)/6,
            // parent/38 confirm the scale) are written from the Bone's own fields, not just carried
            // over from Record's bytes: Record began as a real donor's on-disk bytes for a
            // round-tripped body (where they'd already agree), but a hand-authored body
            // (MarioCustom.cs and its own siblings) has no donor bytes to carry -- its Record is a
            // zeroed 38-byte placeholder, so leaving these three fields unwritten silently produced
            // Start=Pivot=Parent=0 for every bone, an invalid hierarchy Validate() (on the next read)
            // rightly rejects. A bone record also says how many of the normals are its own (offset
            // 18); lit bodies give every point a normal, in point order.
            foreach(var bone in Bones){byte[] r=(byte[])bone.Record.Clone();BitConverter.GetBytes((ushort)(bone.Start*6)).CopyTo(r,0);BitConverter.GetBytes((ushort)bone.Count).CopyTo(r,2);BitConverter.GetBytes((ushort)(bone.Pivot*6)).CopyTo(r,4);BitConverter.GetBytes((short)(bone.Parent<0?-1:bone.Parent*38)).CopyTo(r,6);Array.Clear(r,8,8);Array.Clear(r,18,2);if(Lit)BitConverter.GetBytes((ushort)bone.Count).CopyTo(r,18);w.Write(r);}
            if(normalsOfPoints==null)w.Write((ushort)0); // Solid polygons require no lighting normals.
            else{w.Write((ushort)Vertices.Count);foreach(var n in normalsOfPoints){w.Write(Scaled(n.X,63));w.Write(Scaled(n.Y,63));w.Write(Scaled(n.Z,63));w.Write((ushort)315);}}
            w.Write((ushort)Faces.Count);
            // type 9 = Gouraud lit: colour is the ramp's first entry, the game adds the light; each corner names its point's normal, then the point
            foreach(var f in Faces){w.Write((byte)(Lit?9:0));w.Write((byte)f.Points.Length);w.Write((ushort)f.Colour);foreach(int i in f.Points){if(Lit)w.Write((ushort)i);w.Write((ushort)(i*6));}}
            w.Write((ushort)Lines.Count);foreach(var l in Lines){w.Write((byte)0);w.Write((byte)l.Colour);w.Write((ushort)0);w.Write((ushort)(l.A*6));w.Write((ushort)(l.B*6));}
            w.Write((ushort)Spheres.Count);foreach(var sp in Spheres){w.Write((byte)0);w.Write((byte)sp.Colour);w.Write((ushort)0);w.Write((ushort)sp.Radius);w.Write((ushort)(sp.Point*6));}
        }
        else
        {
            w.Write(new byte[96]);int groups=(int)s.Position;
            foreach(var b in Bones){w.Write((ushort)(b.Parent<0?65535:b.Parent));w.Write((ushort)b.Pivot);w.Write((ushort)b.Count);w.Write((ushort)0);}
            int points=(int)s.Position;
            for(int j=0;j<Bones.Count;j++)for(int i=Bones[j].Start;i<Bones[j].Start+Bones[j].Count;i++){WriteVector(w,Vertices[i]);w.Write((ushort)j);}
            int normals=(int)s.Position;
            if(normalsOfPoints==null)w.Write(new byte[Vertices.Count*8]);
            else for(int j=0;j<Bones.Count;j++)for(int i=Bones[j].Start;i<Bones[j].Start+Bones[j].Count;i++){var n=normalsOfPoints[i];w.Write(Scaled(n.X,10240));w.Write(Scaled(n.Y,10240));w.Write(Scaled(n.Z,10240));w.Write((ushort)j);}
            int polys=(int)s.Position;
            // blocks of triangles / quads, textured ones (type 8, or 10 = Gouraud lit when Lit) apart from the plain ones (type 0, or 4 = Gouraud)
            foreach(var group in Faces.GroupBy(f=>(Quad:f.Points.Length==4,Textured:f.Texture!=null)))
            {
                if(group.Key.Quad==false&&group.First().Points.Length!=3)throw new InvalidDataException("LBA2 requires triangles or quads.");
                bool quad=group.Key.Quad,textured=group.Key.Textured;int stride=textured?(quad?32:24):12;
                w.Write((ushort)((quad?32768:0)|(textured?(Lit?10:8):(Lit?4:0))));w.Write((ushort)group.Count());w.Write(8+group.Count()*stride);
                foreach(var f in group)
                {
                    if(!textured){foreach(int i in f.Points)w.Write((ushort)i);if(!quad)w.Write((ushort)0);w.Write((ushort)f.Colour);w.Write((ushort)0);continue;}
                    var t=f.Texture!;
                    if(quad){foreach(int i in f.Points)w.Write((ushort)i);w.Write((ushort)f.Colour);w.Write((ushort)0);foreach(int uv in t.UV)w.Write((ushort)uv);w.Write((ushort)t.Handle);w.Write((ushort)0);}
                    else{foreach(int i in f.Points)w.Write((ushort)i);w.Write((ushort)t.Handle);w.Write((ushort)f.Colour);w.Write((ushort)0);foreach(int uv in t.UV)w.Write((ushort)uv);}
                }
            }
            int lines=(int)s.Position;foreach(var l in Lines){w.Write((ushort)0);w.Write((ushort)l.Colour);w.Write((ushort)l.A);w.Write((ushort)l.B);}
            int spheres=(int)s.Position;foreach(var sp in Spheres){w.Write((ushort)0);w.Write((ushort)sp.Colour);w.Write((ushort)sp.Point);w.Write((ushort)sp.Radius);}
            int end=(int)s.Position;foreach(uint t in Textures)w.Write(t);
            s.Position=0;w.Write(Static?BitConverter.ToInt32(Header,0):256|(BitConverter.ToInt32(Header,0)&255));w.Write((short)96);w.Write((short)0);
            for(int axis=0;axis<3;axis++){w.Write((int)Math.Floor(world.Min(v=>Component(v,axis))));w.Write((int)Math.Ceiling(world.Max(v=>Component(v,axis))));}
            foreach(int v in new[]{Bones.Count,groups,Vertices.Count,points,Vertices.Count,normals,0,polys,Faces.Count,polys,Lines.Count,lines,Spheres.Count,spheres,Textures.Length,end})w.Write(v);
        }
        return s.ToArray();
    }
    static float Component(Vector3 v,int axis)=>axis==0?v.X:axis==1?v.Y:v.Z;
    static short Checked(float n)=>checked((short)Math.Round(n));
    static void WriteVector(BinaryWriter w,Vector3 v){w.Write(Checked(v.X));w.Write(Checked(v.Y));w.Write(Checked(v.Z));}
}
