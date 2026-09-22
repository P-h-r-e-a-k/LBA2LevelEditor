using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LbaBodyStudio;

// How LBA1 lights a body (LIB_3D P_OB_ISO.ASM ComputeAnimNormal): every normal is turned by its bone's matrix and by the
// actor's facing, dotted with the light direction, and the result (0 .. 15) is added to the polygon's palette index. Flat
// polygons use one normal, Gouraud polygons one per corner, blended across the face.
public sealed class Lba1Shading
{
    public required double[][] BoneMatrices { get; init; }   // 3 x 3, row-major, one per bone (without the actor's own turn)
    public double ActorBeta { get; init; }                   // the actor's facing, 1024ths of a turn
    public double AlphaLight { get; init; }                  // the scene's light angles, 1024ths of a turn
    public double BetaLight { get; init; }

    public float[] Intensities(Body model)
    {
        static double Rad(double a) => a * Math.PI * 2 / 1024;
        // the light: (0, 0, 1) through the same matrix the bones use (turns about x, z, y composed as M = Rx * Rz * Ry)
        double sa = Math.Sin(Rad(AlphaLight)), ca = Math.Cos(Rad(AlphaLight)), sb = Math.Sin(Rad(BetaLight)), cb = Math.Cos(Rad(BetaLight));
        double lx = sb, ly = -sa * cb, lz = ca * cb;
        double sy = Math.Sin(Rad(ActorBeta)), cy = Math.Cos(Rad(ActorBeta));
        var result = new float[model.Normals.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var n = model.Normals[i];
            var m = BoneMatrices[Math.Clamp(i < model.NormalBone.Length ? model.NormalBone[i] : 0, 0, BoneMatrices.Length - 1)];
            double x = m[0]*n.X + m[1]*n.Y + m[2]*n.Z, y = m[3]*n.X + m[4]*n.Y + m[5]*n.Z, z = m[6]*n.X + m[7]*n.Y + m[8]*n.Z;
            double wx = cy*x + sy*z, wz = -sy*x + cy*z;      // then the actor's own turn
            double dot = wx*lx + y*ly + wz*lz;
            result[i] = dot <= 0 || n.Range == 0 ? 0 : (float)Math.Floor(dot * 59 / n.Range);   // the light vector is (0, 0, NORMAL_UNIT - 5) = 59 long
        }
        return result;
    }
}

public static class Renderer
{
    // KeyBackground and KeyGrid are the colours a marker is made transparent by (Lba1ActorImages.Transparent): renders that become markers keep them; a picture shown as it is passes its own.
    public static readonly Color KeyBackground=Color.FromArgb(25,30,39),KeyGrid=Color.FromArgb(44,52,64);
    public static readonly Color ViewBackground=Color.FromArgb(232,240,250),ViewGrid=Color.FromArgb(203,221,240);
    // The rest of the app's own light theme (Theme.xaml), so Body Studio's plain WinForms controls don't look like a different program.
    public static readonly Color PanelBackground=Color.FromArgb(0xE8,0xF0,0xFA),FieldBackground=Color.White,ButtonBackground=Color.FromArgb(0xD6,0xE6,0xF7),
        ButtonBorder=Color.FromArgb(0x9F,0xBE,0xE0),ButtonHover=Color.FromArgb(0xC3,0xDB,0xF5),Accent=Color.FromArgb(0x1B,0x6E,0xC2),
        Border=Color.FromArgb(0xA9,0xC3,0xE0),Text=Color.FromArgb(0x10,0x24,0x3E),TextMuted=Color.FromArgb(0x4E,0x6B,0x8A);
    public static Bitmap Render(Body model,Color[] palette,int width,int height,float yaw,bool wire,bool bones=false,bool headOnly=false,Vector3[]? pose=null,Lba1Shading? shading=null,Color? background=null,Color? gridLine=null)
    {
        width=Math.Max(1,width);height=Math.Max(1,height);
        var bitmap=new Bitmap(Math.Max(1,width),Math.Max(1,height));using var g=Graphics.FromImage(bitmap);
        g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(background??KeyBackground);
        var neutral=model.World();var world=pose??neutral;float h=Math.Max(1,neutral.Max(v=>v.Y)-neutral.Min(v=>v.Y));float minY=neutral.Min(v=>v.Y);
        if(headOnly){minY+=h*.82f;h*=.18f;}
        float visibleWidth=headOnly?h*.85f:neutral.Max(v=>v.X)-neutral.Min(v=>v.X);
        float scale=Math.Min(height*0.80f/h,width*0.80f/Math.Max(h*0.65f,visibleWidth));
        var focus=headOnly&&model.Bones.Count>14?new Vector3(world[model.Bones[14].Pivot].X,0,0):Vector3.Zero;
        var rotated=world.Select(v=>new Vector3((v.X-focus.X)*MathF.Cos(yaw)+(v.Z-focus.Z)*MathF.Sin(yaw),v.Y-minY,-(v.X-focus.X)*MathF.Sin(yaw)+(v.Z-focus.Z)*MathF.Cos(yaw))).ToArray();
        PointF Screen(Vector3 v)=>new(width/2f+v.X*scale,height*0.90f-v.Y*scale+v.Z*scale*0.08f);
        using var grid=new Pen(gridLine??KeyGrid);
        for(int x=-5;x<=5;x++)g.DrawLine(grid,width/2f+x*h*scale/6,height*0.92f,width/2f+x*h*scale/6,height*0.97f);
        g.DrawLine(grid,20,height*0.92f,width-20,height*0.92f);
        // Polygon-average painter sorting loses narrow lettering at oblique angles.
        // Rasterize the actual surfaces with interpolated depth instead.
        var depth=Enumerable.Repeat(float.PositiveInfinity,width*height).ToArray();
        var pixels=new int[width*height];
        float Edge(PointF a,PointF b,float x,float y)=>(x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);
        var lit=shading!=null&&model.Game==1&&model.Normals.Count>0?shading.Intensities(model):null;
        // A generated body carries game lighting (Body.Lit): preview it with a light from above-left of the viewer, the game adds up to its
        // maximum number of ramp steps to each polygon's start colour.
        float[]? previewLight=null;
        if(lit==null&&(model.Lit||model.Faces.Any(x=>x.Material>=0)))
        {
            var normals=model.VertexNormals();var toLight=Vector3.Normalize(new Vector3(-0.35f,0.55f,-0.75f));float max=LightModel.Max(model.Game);
            previewLight=normals.Select(n=>{var r=new Vector3(n.X*MathF.Cos(yaw)+n.Z*MathF.Sin(yaw),n.Y,-n.X*MathF.Sin(yaw)+n.Z*MathF.Cos(yaw));return Math.Clamp(Vector3.Dot(r,toLight),0,1)*max;}).ToArray();
        }
        foreach(var f in model.Faces)
        {
            int colour=palette[Math.Clamp(f.Colour,0,255)].ToArgb();
            bool faceLit=previewLight!=null&&LightModel.IsLit(f,model.Game,model.Lit);
            // the game's lighting: flat faces take one intensity, Gouraud faces one per corner (blended below)
            float[]? corner=null;
            if(lit!=null&&f.Material>=7)
            {
                corner=new float[f.Points.Length];
                for(int k=0;k<corner.Length;k++)
                {
                    int normal=f.PointNormals!=null?f.PointNormals[k]:f.FaceNormal;
                    corner[k]=normal>=0&&normal<lit.Length?lit[normal]:0;
                }
                if(f.Material<9)colour=palette[Math.Clamp(f.Colour+(int)corner[0],0,255)].ToArgb();
            }
            for(int t=1;t<f.Points.Length-1;t++)
            {
                var a=rotated[f.Points[0]];var b=rotated[f.Points[t]];var c=rotated[f.Points[t+1]];
                var pa=Screen(a);var pb=Screen(b);var pc=Screen(c);float area=Edge(pa,pb,pc.X,pc.Y);
                if(Math.Abs(area)<.001f)continue;
                int x0=Math.Max(0,(int)MathF.Floor(Math.Min(pa.X,Math.Min(pb.X,pc.X)))),x1=Math.Min(width-1,(int)MathF.Ceiling(Math.Max(pa.X,Math.Max(pb.X,pc.X))));
                int y0=Math.Max(0,(int)MathF.Floor(Math.Min(pa.Y,Math.Min(pb.Y,pc.Y)))),y1=Math.Min(height-1,(int)MathF.Ceiling(Math.Max(pa.Y,Math.Max(pb.Y,pc.Y))));
                for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                {
                    float wa=Edge(pb,pc,x+.5f,y+.5f)/area,wb=Edge(pc,pa,x+.5f,y+.5f)/area,wc=1-wa-wb;
                    if(wa<-.0001f||wb<-.0001f||wc<-.0001f)continue;
                    float z=wa*a.Z+wb*b.Z+wc*c.Z;int index=y*width+x;
                    if(z<=depth[index])
                    {
                        depth[index]=z;
                        if(f.Texture!=null&&model.TexturePage!=null&&f.Texture.Handle<model.Textures.Length)
                        {
                            // textured polygon: (U, V) are 8.8 fixed point pixels of the 256 x 256 page; the table entry gives the offset and a repeat mask per axis
                            var uv=f.Texture.UV;int a0=0,b0=t,c0=t+1;
                            float u=wa*uv[a0*2]+wb*uv[b0*2]+wc*uv[c0*2],v=wa*uv[a0*2+1]+wb*uv[b0*2+1]+wc*uv[c0*2+1];
                            uint info=model.Textures[f.Texture.Handle];int mask=(int)(info>>16);
                            int tx=((int)u>>8)&(mask&0xFF),ty=((int)v>>8)&((mask>>8)&0xFF);
                            int texel=model.TexturePage[((int)(info&0xFFFF)+ty*256+tx)&0xFFFF];
                            float shade=faceLit?wa*previewLight[f.Points[0]]+wb*previewLight[f.Points[t]]+wc*previewLight[f.Points[t+1]]:0;
                            pixels[index]=palette[Math.Clamp(texel+(int)Math.Round(shade),0,255)].ToArgb();
                        }
                        else if(faceLit){float shade=wa*previewLight[f.Points[0]]+wb*previewLight[f.Points[t]]+wc*previewLight[f.Points[t+1]];pixels[index]=palette[Math.Clamp(f.Colour+(int)Math.Round(shade),0,255)].ToArgb();}
                        else if(corner!=null&&f.Material>=9){float shade=wa*corner[0]+wb*corner[t]+wc*corner[t+1];pixels[index]=palette[Math.Clamp(f.Colour+(int)Math.Round(shade),0,255)].ToArgb();}
                        else pixels[index]=colour;
                    }
                }
            }
        }
        // Lines and spheres (hair buns, hands, necklaces) go through the same depth buffer as the polygons, so a
        // bun behind the head stays behind it instead of being painted over the face.
        void Plot(int x,int y,float z,int colour){if(x<0||y<0||x>=width||y>=height)return;int i=y*width+x;if(z<=depth[i]){depth[i]=z;pixels[i]=colour;}}
        foreach(var l in model.Lines)
        {
            var a=rotated[l.A];var b=rotated[l.B];var pa=Screen(a);var pb=Screen(b);int colour=palette[Math.Clamp(l.Colour,0,255)].ToArgb();
            int steps=Math.Max(1,(int)MathF.Ceiling(Math.Max(Math.Abs(pb.X-pa.X),Math.Abs(pb.Y-pa.Y))));
            for(int k=0;k<=steps;k++)
            {
                float t=k/(float)steps;int x=(int)MathF.Round(pa.X+(pb.X-pa.X)*t),y=(int)MathF.Round(pa.Y+(pb.Y-pa.Y)*t);float z=a.Z+(b.Z-a.Z)*t-2f;
                Plot(x,y,z,colour);Plot(x+1,y,z,colour);
            }
        }
        foreach(var s in model.Spheres)
        {
            var c=rotated[s.Point];var p=Screen(c);float r=Math.Max(1f,s.Radius*scale);int colour=palette[Math.Clamp(s.Colour,0,255)].ToArgb();
            for(int y=(int)MathF.Floor(p.Y-r);y<=(int)MathF.Ceiling(p.Y+r);y++)for(int x=(int)MathF.Floor(p.X-r);x<=(int)MathF.Ceiling(p.X+r);x++)
            {
                float dx=x+.5f-p.X,dy=y+.5f-p.Y,d2=dx*dx+dy*dy;if(d2>r*r)continue;
                Plot(x,y,c.Z-MathF.Sqrt(r*r-d2)/scale,colour);
            }
        }
        using(var layer=new Bitmap(width,height,PixelFormat.Format32bppArgb))
        {
            var locked=layer.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try{Marshal.Copy(pixels,0,locked.Scan0,pixels.Length);}finally{layer.UnlockBits(locked);}
            g.DrawImageUnscaled(layer,0,0);
        }
        if(wire){using var pen=new Pen(Color.FromArgb(130,110,185,210),0.8f);foreach(var f in model.Faces)g.DrawPolygon(pen,f.Points.Select(i=>Screen(rotated[i])).ToArray());}
        if(bones)
        {
            using var pen=new Pen(Color.FromArgb(255,195,74),2);using var brush=new SolidBrush(Color.FromArgb(255,195,74));
            for(int i=1;i<model.Bones.Count;i++) {var b=model.Bones[i];var parent=model.Bones[b.Parent];var a=Screen(rotated[b.Pivot]);var z=Screen(rotated[parent.Pivot]);g.DrawLine(pen,a,z);g.FillEllipse(brush,a.X-3,a.Y-3,6,6);g.DrawString(i.ToString(),SystemFonts.SmallCaptionFont!,brush,a);}
        }
        return bitmap;
    }
}

public sealed class ModelView : Control
{
    public Generated? Model;
    public float Yaw;
    public bool Wire,Bones;
    public bool HeadOnly;
    Point? drag;
    public ModelView(){DoubleBuffered=true;BackColor=Renderer.ViewBackground;SetStyle(ControlStyles.ResizeRedraw,true);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if(Model==null){TextRenderer.DrawText(e.Graphics,"Generate a body to preview it here",Font,ClientRectangle,Color.Silver,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
        using var bitmap=Renderer.Render(Model.Body,Model.Palette,Width,Height,Yaw,Wire,Bones,HeadOnly,background:Renderer.ViewBackground,gridLine:Renderer.ViewGrid);e.Graphics.DrawImageUnscaled(bitmap,0,0);
        TextRenderer.DrawText(e.Graphics,$"LBA{Model.Body.Game}  •  {Model.Body.Vertices.Count} points  •  {Model.Body.Faces.Count} polygons  •  {Model.Body.Bones.Count} bones",Font,new Point(16,16),Color.LightGray);
        TextRenderer.DrawText(e.Graphics,"Drag to rotate  |  Neutral pose  |  Palette colours",Font,new Point(16,Height-32),Color.LightGray);
    }
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);drag=e.Location;Capture=true;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(drag is Point p){Yaw+=(e.X-p.X)*0.012f;drag=e.Location;Invalidate();}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);drag=null;Capture=false;}
}

