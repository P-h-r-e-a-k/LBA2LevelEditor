using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LbaBodyStudio;

public static class Renderer
{
    public static Bitmap Render(Body model,Color[] palette,int width,int height,float yaw,bool wire,bool bones=false,bool headOnly=false)
    {
        width=Math.Max(1,width);height=Math.Max(1,height);
        var bitmap=new Bitmap(Math.Max(1,width),Math.Max(1,height));using var g=Graphics.FromImage(bitmap);
        g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(25,30,39));
        var world=model.World();float h=Math.Max(1,world.Max(v=>v.Y)-world.Min(v=>v.Y));float minY=world.Min(v=>v.Y);
        if(headOnly){minY+=h*.82f;h*=.18f;}
        float visibleWidth=headOnly?h*.85f:world.Max(v=>v.X)-world.Min(v=>v.X);
        float scale=Math.Min(height*0.80f/h,width*0.80f/Math.Max(h*0.65f,visibleWidth));
        var focus=headOnly&&model.Bones.Count>14?new Vector3(world[model.Bones[14].Pivot].X,0,0):Vector3.Zero;
        var rotated=world.Select(v=>new Vector3((v.X-focus.X)*MathF.Cos(yaw)+(v.Z-focus.Z)*MathF.Sin(yaw),v.Y-minY,-(v.X-focus.X)*MathF.Sin(yaw)+(v.Z-focus.Z)*MathF.Cos(yaw))).ToArray();
        PointF Screen(Vector3 v)=>new(width/2f+v.X*scale,height*0.90f-v.Y*scale+v.Z*scale*0.08f);
        using var grid=new Pen(Color.FromArgb(44,52,64));
        for(int x=-5;x<=5;x++)g.DrawLine(grid,width/2f+x*h*scale/6,height*0.92f,width/2f+x*h*scale/6,height*0.97f);
        g.DrawLine(grid,20,height*0.92f,width-20,height*0.92f);
        // Polygon-average painter sorting loses narrow lettering at oblique angles.
        // Rasterize the actual surfaces with interpolated depth instead.
        var depth=Enumerable.Repeat(float.PositiveInfinity,width*height).ToArray();
        var pixels=new int[width*height];
        float Edge(PointF a,PointF b,float x,float y)=>(x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);
        foreach(var f in model.Faces)
        {
            int colour=palette[Math.Clamp(f.Colour,0,255)].ToArgb();
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
                    if(z<=depth[index]){depth[index]=z;pixels[index]=colour;}
                }
            }
        }
        using(var layer=new Bitmap(width,height,PixelFormat.Format32bppArgb))
        {
            var locked=layer.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try{Marshal.Copy(pixels,0,locked.Scan0,pixels.Length);}finally{layer.UnlockBits(locked);}
            g.DrawImageUnscaled(layer,0,0);
        }
        if(wire){using var pen=new Pen(Color.FromArgb(130,110,185,210),0.8f);foreach(var f in model.Faces)g.DrawPolygon(pen,f.Points.Select(i=>Screen(rotated[i])).ToArray());}
        foreach(var l in model.Lines){using var pen=new Pen(palette[l.Colour],1.4f);g.DrawLine(pen,Screen(rotated[l.A]),Screen(rotated[l.B]));}
        foreach(var s in model.Spheres){var p=Screen(rotated[s.Point]);float r=s.Radius*scale;using var brush=new SolidBrush(palette[s.Colour]);g.FillEllipse(brush,p.X-r,p.Y-r,r*2,r*2);}
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
    public ModelView(){DoubleBuffered=true;BackColor=Color.FromArgb(25,30,39);SetStyle(ControlStyles.ResizeRedraw,true);}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if(Model==null){TextRenderer.DrawText(e.Graphics,"Generate a body to preview it here",Font,ClientRectangle,Color.Silver,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}
        using var bitmap=Renderer.Render(Model.Body,Model.Palette,Width,Height,Yaw,Wire,Bones,HeadOnly);e.Graphics.DrawImageUnscaled(bitmap,0,0);
        TextRenderer.DrawText(e.Graphics,$"LBA{Model.Body.Game}  •  {Model.Body.Vertices.Count} points  •  {Model.Body.Faces.Count} polygons  •  {Model.Body.Bones.Count} bones",Font,new Point(16,16),Color.LightGray);
        TextRenderer.DrawText(e.Graphics,"Drag to rotate  |  Neutral pose  |  Palette colours",Font,new Point(16,Height-32),Color.LightGray);
    }
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);drag=e.Location;Capture=true;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(drag is Point p){Yaw+=(e.X-p.X)*0.012f;drag=e.Location;Invalidate();}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);drag=null;Capture=false;}
}

