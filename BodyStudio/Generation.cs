using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace LbaBodyStudio;

public sealed class Settings
{
    // LBAAssembler already knows where the user's LBA2 install lives
    // (EditorSettings, configurable via the Settings window) -- reuse it as
    // this standalone tool's own default rather than the original app's
    // hardcoded developer-machine path. LBA1 isn't wired into the level
    // editor yet (a later addition, per its own README), so there's no
    // equivalent known-good default for it here; the Browse button still
    // lets a user point at one.
    public string ImagePath { get; set; } = "";
    public string Lba1Folder { get; set; } = "";
    public string Lba2Folder { get; set; } = LBAAssembler.EditorSettings.Current.GameDirectory;
    public int Lba1Body { get; set; } = 0;
    public int Lba2Body { get; set; } = 0;
    public string Target { get; set; } = "Both";
    public string Layout { get; set; } = "Front + back";
    public string Method { get; set; } = "New humanoid";
    public string Mask { get; set; } = "Dark subject";
    public int Threshold { get; set; } = 45;
    public bool AutoCrop { get; set; } = true;
    public bool FlipFront { get; set; }
    // The native engine's own local +Z is a character's forward-facing/
    // walking direction (derived from IMATSTDF.CPP/LROT3DF.CPP's Beta
    // rotation matrix, INTERDEP.CPP's root-motion translation, and
    // CAMERA.CPP's documented "BetaCam = 2048 - heroBeta" behind-the-hero
    // chase-camera rule, which only resolves to a camera looking along the
    // character's own +Z -- i.e. viewing their back -- if +Z is the
    // direction they walk/face). A character's face therefore sits at
    // positive local Z, not negative -- this default was originally true
    // (front at negative Z), which is backwards relative to the real
    // engine: confirmed by a user-supplied front/back reference pair
    // (bandana text + grin on the front, tied bow on the back) generating
    // with the two swapped. Every place that reads this setting (Sample's
    // isFront, the Front/Back preview buttons, HeadDecoration's own
    // Point() mirroring, and head-front.png/head-back.png's export yaw) is
    // already driven consistently off this one flag, so correcting the
    // default here is the complete fix.
    public bool NegativeZFront { get; set; } = false;
    // Write the game's lighting data (vertex normals, Gouraud polygons on ramp-start colours) so the body is shaded like the game's own
    // characters, instead of flat unlit polygons.
    public bool Lit { get; set; } = true;
    public float Fit { get; set; } = 0.8f;
    public float Height { get; set; } = 1f;
    public float Width { get; set; } = 1f;
    public float Depth { get; set; } = 1f;
    public float HeadScale { get; set; } = 0.7f;
    public int DetailBudget { get; set; } = 460;
    public bool HeadDetails { get; set; }
    public string BandanaText { get; set; } = "Phreak";
    public bool ArchiveCopy { get; set; } = true;
    public string OutputFolder { get; set; } = "";
}

public sealed class ReferenceImage : IDisposable
{
    public Bitmap Bitmap { get; }
    public Rectangle Crop { get; }
    public (float Left,float Right)[] Rows { get; }
    public List<(float Left,float Right)>[] Runs { get; }
    public ReferenceImage(Bitmap source,Rectangle region,Settings settings)
    {
        // Work at bounded resolution; projection keeps the original image pixels.
        Bitmap=source.Clone(region,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var small=new Bitmap(Bitmap,new Size(Math.Min(384,Bitmap.Width),Math.Min(768,Bitmap.Height)));
        int w=small.Width,h=small.Height;
        Color bg=small.GetPixel(0,0);
        bool[,] mask=new bool[w,h];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            var c=small.GetPixel(x,y);int lum=(c.R*299+c.G*587+c.B*114)/1000;
            mask[x,y]=settings.Mask switch
            {
                "Transparent background"=>c.A>128,
                "Light subject"=>c.A>128&&lum>255-settings.Threshold,
                "Background colour"=>c.A>128&&Math.Max(Math.Abs(c.R-bg.R),Math.Max(Math.Abs(c.G-bg.G),Math.Abs(c.B-bg.B)))>settings.Threshold,
                _=>c.A>128&&lum<settings.Threshold
            };
        }
        // Ignore foreground components touching the edge (dark backdrop corners).
        bool[,] seen=new bool[w,h];var components=new List<List<Point>>();
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            if(seen[x,y]||!mask[x,y])continue;
            var list=new List<Point>();var queue=new Queue<Point>();queue.Enqueue(new(x,y));seen[x,y]=true;bool edge=false;
            while(queue.Count>0)
            {
                var a=queue.Dequeue();list.Add(a);edge|=a.X==0||a.Y==0||a.X==w-1||a.Y==h-1;
                foreach(var d in new[]{new Point(1,0),new Point(-1,0),new Point(0,1),new Point(0,-1)})
                { int xx=a.X+d.X,yy=a.Y+d.Y;if(xx>=0&&xx<w&&yy>=0&&yy<h&&!seen[xx,yy]&&mask[xx,yy]){seen[xx,yy]=true;queue.Enqueue(new(xx,yy));} }
            }
            if(!edge&&list.Count>30)components.Add(list);
        }
        if(!settings.AutoCrop)
        {
            Crop=new(0,0,Bitmap.Width,Bitmap.Height);
        }
        else
        {
            var biggest=components.OrderByDescending(c=>c.Count).FirstOrDefault();
            if(biggest==null||biggest.Count<w*h/100)throw new InvalidDataException("No clear subject found. Change the mask/threshold, use a tightly cropped image and turn off Auto crop, or use a transparent PNG.");
            int minX=biggest.Min(p=>p.X),maxX=biggest.Max(p=>p.X),minY=biggest.Min(p=>p.Y),maxY=biggest.Max(p=>p.Y);
            // Retain a disconnected head above a white band, within the main silhouette's width.
            foreach(var c in components)
            {
                if(c.Count<biggest.Count/100)continue;
                int cx=c.Sum(p=>p.X)/c.Count,cy=c.Sum(p=>p.Y)/c.Count;
                if(cx>minX&&cx<maxX&&cy<maxY&&c.Max(p=>p.Y)>=minY-h/6){minY=Math.Min(minY,c.Min(p=>p.Y));}
            }
            Crop=Rectangle.FromLTRB(minX*Bitmap.Width/w,minY*Bitmap.Height/h,Math.Min(Bitmap.Width,(maxX+1)*Bitmap.Width/w),Math.Min(Bitmap.Height,(maxY+1)*Bitmap.Height/h));
        }
        Rows=new (float,float)[256];Runs=new List<(float,float)>[256];
        for(int r=0;r<Rows.Length;r++)
        {
            int y=Math.Clamp((int)((Crop.Top+((r+0.5f)/256)*Crop.Height)*h/Bitmap.Height),0,h-1);
            int a=Math.Clamp(Crop.Left*w/Bitmap.Width,0,w-1),z=Math.Clamp(Crop.Right*w/Bitmap.Width-1,0,w-1);
            int left=z,right=a;Runs[r]=[];int runStart=-1;
            for(int x=a;x<=z+1;x++)
            {
                bool on=x<=z&&mask[x,y];
                if(on){left=Math.Min(left,x);right=Math.Max(right,x);if(runStart<0)runStart=x;}
                else if(runStart>=0){if(x-runStart>2)Runs[r].Add(((float)(runStart-a)/(z-a+1),(float)(x-a)/(z-a+1)));runStart=-1;}
            }
            Rows[r]=right>=left?((float)(left-a)/(z-a+1),(float)(right-a+1)/(z-a+1)):(0.35f,0.65f);
        }
    }
    public Color Sample(float u,float v)
    {
        int x=Math.Clamp(Crop.Left+(int)(u*(Crop.Width-1)),Crop.Left,Crop.Right-1);
        int y=Math.Clamp(Crop.Top+(int)(v*(Crop.Height-1)),Crop.Top,Crop.Bottom-1);
        return Bitmap.GetPixel(x,y);
    }
    public void Dispose()=>Bitmap.Dispose();
}

public sealed record Generated(Body Body,Color[] Palette,int TemplateIndex,string SourceArchive,string ImagePath);

public static class Generator
{
    public static string BodyArchive(string folder)
    {
        // Prefer the user's clean backup when an installed archive has been modded.
        string backup=Path.Combine(folder,"OrigBODY.HQR");
        return File.Exists(backup)?backup:Path.Combine(folder,"BODY.HQR");
    }
    public static Color[] Palette(string folder)
    {
        var bytes=new Hqr(Path.Combine(folder,"RESS.HQR")).Read(0);
        if(bytes.Length!=768)throw new InvalidDataException("Expected a 256-colour RGB palette in RESS.HQR entry 0.");
        return Enumerable.Range(0,256).Select(i=>Color.FromArgb(bytes[i*3],bytes[i*3+1],bytes[i*3+2])).ToArray();
    }
    public static Generated Generate(Settings settings,int game)
    {
        if(settings.Layout is not ("Front + back" or "Single front")||settings.Method is not ("New humanoid" or "Fit template"))throw new InvalidDataException("Unsupported image layout or generation method.");
        if(settings.HeadDetails&&settings.Method!="New humanoid")throw new InvalidDataException("Bandana and teeth mode requires New humanoid mesh generation.");
        if(settings.HeadDetails)HeadDecoration.ValidateText(settings.BandanaText);
        if(settings.Mask is not ("Dark subject" or "Light subject" or "Transparent background" or "Background colour"))throw new InvalidDataException("Unsupported mask method.");
        if(settings.Threshold<1||settings.Threshold>254||settings.DetailBudget<0||settings.DetailBudget>540||!float.IsFinite(settings.Height+settings.Width+settings.Depth+settings.HeadScale+settings.Fit)||settings.Height<.25f||settings.Height>3||settings.Width<.25f||settings.Width>3||settings.Depth<.25f||settings.Depth>3||settings.HeadScale<.25f||settings.HeadScale>1.5f||settings.Fit<0||settings.Fit>1)throw new InvalidDataException("Image fitting settings are outside the supported range.");
        string folder=game==1?settings.Lba1Folder:settings.Lba2Folder,archive=BodyArchive(folder);
        int index=game==1?settings.Lba1Body:settings.Lba2Body;
        var model=Body.Read(new Hqr(archive).Read(index),game);var palette=Palette(folder);
        var donorWorld=model.World();
        var jointOrigins=model.Bones.Select(b=>b.Parent<0?Vector3.Zero:donorWorld[b.Pivot]).ToArray();
        using var original=new Bitmap(settings.ImagePath);
        bool split=settings.Layout=="Front + back";
        using var front=new ReferenceImage(original,new(0,0,split?original.Width/2:original.Width,original.Height),settings);
        using var back=split?new ReferenceImage(original,new(original.Width/2,0,original.Width-original.Width/2,original.Height),settings):null;
        if(settings.Method=="New humanoid")model=Humanoid.Build(model,front,settings);
        var world=model.World();float minY=world.Min(v=>v.Y),height=world.Max(v=>v.Y)-minY;
        float minX=world.Min(v=>v.X),maxX=world.Max(v=>v.X),centreX=(minX+maxX)/2;
        float meshWidth=maxX-minX;
        if(height<=0||meshWidth<=0)throw new InvalidDataException("Template has no usable height/width.");
        // Reduce a stylised template head while retaining its original bone layout.
        float neck=minY+height*0.77f,headTop=world.Max(v=>v.Y);
        float headScale=settings.Method=="New humanoid"?1:settings.HeadScale;
        float newHeight=neck-minY+(headTop-neck)*headScale;
        for(int i=0;i<world.Length;i++)
        {
            var v=world[i];if(v.Y>neck){float amount=Math.Clamp((v.Y-neck)/(height*0.06f),0,1);v.X=centreX+(v.X-centreX)*(1-amount*(1-headScale));v.Z*=1-amount*(1-headScale);v.Y=neck+(v.Y-neck)*headScale;}world[i]=v;
        }
        var baseWorld=(Vector3[])world.Clone();
        float targetWidth=newHeight*front.Crop.Width/front.Crop.Height;
        for(int i=0;i<world.Length;i++)
        {
            var v=baseWorld[i];float yNorm=(v.Y-minY)/newHeight;
            var row=front.Rows[Math.Clamp((int)((1-yNorm)*255),0,255)];
            float desiredWidth=targetWidth*(row.Right-row.Left);
            // Estimate source cross-section from nearby template vertices.
            var nearby=baseWorld.Where(p=>Math.Abs(p.Y-v.Y)<newHeight*0.04f).ToArray();
            float sourceHalf=nearby.Length>0?nearby.Max(p=>Math.Abs(p.X-centreX)):meshWidth/2;
            float factor=Math.Clamp(desiredWidth/Math.Max(20,sourceHalf*2),0.45f,1.8f);
            factor=settings.Method=="New humanoid"?1:1+(factor-1)*settings.Fit;
            v.X=(v.X-(settings.Method=="New humanoid"?0:centreX))*factor*settings.Width;
            v.Y=(v.Y-minY)*settings.Height;
            v.Z*=settings.Depth;
            world[i]=v;
        }
        // Mesh scale and image fitting must never move the animation skeleton.
        for(int b=1;b<model.Bones.Count;b++)world[model.Bones[b].Pivot]=jointOrigins[b];
        model.SetWorld(world);
        float outputHeight=world.Max(v=>v.Y),projectionWidth=targetWidth*settings.Width;
        Color Sample(Vector3 p)
        {
            bool isFront=settings.NegativeZFront?p.Z<=0:p.Z>=0;
            var reference=isFront||back==null?front:back;
            float u=0.5f+p.X/Math.Max(1,projectionWidth);
            if(!isFront&&back!=null)u=1-u;
            if(settings.FlipFront)u=1-u;
            float v=Math.Clamp(1-p.Y/outputHeight,0,1);
            // Keep silhouette edges from picking up the background halo. Do not mask interior art.
            var runs=reference.Runs[Math.Clamp((int)(v*255),0,255)];
            if(runs.Count>0&&v>0.17f&&!runs.Any(r=>u>=r.Left&&u<=r.Right))
            {
                var nearest=runs.OrderBy(r=>Math.Min(Math.Abs(u-r.Left),Math.Abs(u-r.Right))).First();
                u=Math.Clamp(u,nearest.Left+Math.Min(0.01f,(nearest.Right-nearest.Left)/3),nearest.Right-Math.Min(0.01f,(nearest.Right-nearest.Left)/3));
            }
            return reference.Sample(Math.Clamp(u,0,1),v);
        }
        int NearestColour(Color c)
        {
            int best=1;double score=double.MaxValue;
            // Index zero is reserved by a number of asset tools; use another black when possible.
            for(int i=1;i<256;i++){var q=palette[i];double d=(c.R-q.R)*(c.R-q.R)+(c.G-q.G)*(c.G-q.G)+(c.B-q.B)*(c.B-q.B);if(d<score){score=d;best=i;}}
            return best;
        }
        int Colour(Vector3 p)=>NearestColour(Sample(p));
        if(!settings.HeadDetails)AddDetail(model,world,Sample,Math.Min(settings.DetailBudget,model.Limit-10));
        world=model.World();
        // Lit polygons are shaded by adding the light (0..~11 ramp steps) to their colour, so their colour is the bottom of the ramp the picture's colour sits in.
        int Face(int index)=>settings.Lit?LitBase(index,game):index;
        for(int i=0;i<model.Faces.Count;i++){var f=model.Faces[i];model.Faces[i]=f with{Colour=Face(f.DetailTone>=0?NearestColour(Color.FromArgb(f.DetailTone,f.DetailTone,f.DetailTone)):Colour(f.Points.Select(p=>world[p]).Aggregate(Vector3.Zero,(a,b)=>a+b)/f.Points.Length))};}
        for(int i=0;i<model.Lines.Count;i++){var l=model.Lines[i];model.Lines[i]=l with{Colour=Colour((world[l.A]+world[l.B])/2)};}
        for(int i=0;i<model.Spheres.Count;i++){var sp=model.Spheres[i];model.Spheres[i]=sp with{Colour=Colour(world[sp.Point]),Radius=(int)Math.Round(sp.Radius*Math.Min(settings.Width,settings.Depth))};}
        // Quantize through the native representation used by the exported preview.
        model.Lit=settings.Lit;
        model=Body.Read(model.Write(),game);model.Lit=settings.Lit;
        return new(model,palette,index,archive,settings.ImagePath);
    }
    // See LightModel: a picture colour is what shows on screen, the body stores the ramp start the game's light lifts to it.
    public static int LitBase(int index,int game)=>LightModel.BaseOf(index,game);
    static void AddDetail(Body model,Vector3[] world,Func<Vector3,Color> sample,int budget)
    {
        var points=world.ToList();var owners=new List<int>();
        for(int i=0;i<model.Bones.Count;i++)owners.AddRange(Enumerable.Repeat(i,model.Bones[i].Count));
        // Refine colour boundaries only within a single rigid bone. Added vertices never alter joints.
        while(model.Faces.Count+model.Lines.Count+model.Spheres.Count<budget-3&&points.Count<model.Limit-1)
        {
            int best=-1;double bestScore=450;
            for(int i=0;i<model.Faces.Count;i++)
            {
                var f=model.Faces[i];if(f.Points.Any(p=>owners[p]!=owners[f.Points[0]]))continue;
                if(model.Game==1&&model.DrawBufferBytes+f.Points.Length*22-(4+f.Points.Length*6)>9900)continue;
                var centre=f.Points.Select(p=>points[p]).Aggregate(Vector3.Zero,(a,b)=>a+b)/f.Points.Length;
                var colours=f.Points.Select(p=>sample(points[p]*0.75f+centre*0.25f)).Append(sample(centre)).ToArray();
                double variance=colours.Max(c=>c.R+c.G+c.B)-colours.Min(c=>c.R+c.G+c.B);
                var a=points[f.Points[0]];var b=points[f.Points[1]];var c=points[f.Points[2]];
                double area=Vector3.Cross(b-a,c-a).Length()/2;
                double score=variance*Math.Sqrt(area);
                if(score>bestScore&&area>12){bestScore=score;best=i;}
            }
            if(best<0)break;
            var face=model.Faces[best];var mid=face.Points.Select(p=>points[p]).Aggregate(Vector3.Zero,(a,b)=>a+b)/face.Points.Length;
            int id=points.Count;points.Add(mid);owners.Add(owners[face.Points[0]]);model.Faces.RemoveAt(best);
            for(int j=0;j<face.Points.Length;j++)model.Faces.Add(new([face.Points[j],face.Points[(j+1)%face.Points.Length],id],face.Colour));
        }
        // Restore contiguous vertex ranges per bone and remap every pivot and primitive.
        int[] order=Enumerable.Range(0,points.Count).OrderBy(i=>owners[i]).ToArray(),map=new int[points.Count];
        for(int i=0;i<order.Length;i++)map[order[i]]=i;
        int start=0;
        for(int i=0;i<model.Bones.Count;i++)
        {
            var old=model.Bones[i];int count=owners.Count(b=>b==i);byte[] record=(byte[])old.Record.Clone();
            if(model.Game==1){BitConverter.GetBytes((ushort)(start*6)).CopyTo(record,0);BitConverter.GetBytes((ushort)count).CopyTo(record,2);BitConverter.GetBytes((ushort)(map[old.Pivot]*6)).CopyTo(record,4);}
            model.Bones[i]=new(start,count,map[old.Pivot],old.Parent,record);start+=count;
        }
        model.Faces=model.Faces.Select(f=>f with{Points=f.Points.Select(p=>map[p]).ToArray()}).ToList();
        model.Lines=model.Lines.Select(l=>l with{A=map[l.A],B=map[l.B]}).ToList();
        model.Spheres=model.Spheres.Select(s=>s with{Point=map[s.Point]}).ToList();
        model.Vertices=Enumerable.Repeat(Vector3.Zero,points.Count).ToList();model.SetWorld(order.Select(i=>points[i]).ToArray());
    }
    public static string Export(Settings settings,IEnumerable<Generated> models)
    {
        if(string.IsNullOrWhiteSpace(settings.OutputFolder))throw new InvalidDataException("Choose an output folder.");
        string root=Path.GetFullPath(settings.OutputFolder);
        foreach(var m in models)
        {
            string installed=Path.GetFullPath(Path.GetDirectoryName(m.SourceArchive)!);
            if(root.Equals(installed,StringComparison.OrdinalIgnoreCase)||root.StartsWith(installed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose an output folder outside the installed game. Exports contain a separate mod archive.");
        }
        Directory.CreateDirectory(root);
        string run=Path.Combine(root,"Body-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..4]);Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run,"project.json"),JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true}));
        foreach(var g in models)
        {
            string dir=Path.Combine(run,$"LBA{g.Body.Game}");Directory.CreateDirectory(dir);
            byte[] bytes=g.Body.Write();File.WriteAllBytes(Path.Combine(dir,"character.body"),bytes);
            Body.Read(bytes,g.Body.Game).Validate();
            if(settings.ArchiveCopy)
            {
                var source=new Hqr(g.SourceArchive);byte[] archive=source.Replace(g.TemplateIndex,bytes);var check=new Hqr(archive);
                if(!check.Read(g.TemplateIndex).SequenceEqual(bytes))throw new InvalidDataException("Archive verification failed.");
                File.WriteAllBytes(Path.Combine(dir,"BODY.HQR"),archive);
            }
            ExportObj(g,dir);
            using var preview=Renderer.Render(g.Body,g.Palette,900,1000,0.4f,false);preview.Save(Path.Combine(dir,"preview.png"));
            if(settings.HeadDetails)
            {
                float frontYaw=settings.NegativeZFront?0:MathF.PI;
                using var headFront=Renderer.Render(g.Body,g.Palette,900,700,frontYaw,false,headOnly:true);headFront.Save(Path.Combine(dir,"head-front.png"));
                using var headBack=Renderer.Render(g.Body,g.Palette,900,700,frontYaw+MathF.PI,false,headOnly:true);headBack.Save(Path.Combine(dir,"head-back.png"));
                using var headAngle=Renderer.Render(g.Body,g.Palette,900,700,frontYaw+.55f,false,headOnly:true);headAngle.Save(Path.Combine(dir,"head-angle.png"));
            }
            File.WriteAllText(Path.Combine(dir,"manifest.json"),JsonSerializer.Serialize(new{game=$"LBA{g.Body.Game}",bodyIndex=g.TemplateIndex,sourceArchive=g.SourceArchive,sourceImage=g.ImagePath,archiveCompression="HQR method 1",skeleton="Donor rest joint positions and hierarchy preserved",vertices=g.Body.Vertices.Count,polygons=g.Body.Faces.Count,bones=g.Body.Bones.Count,headDetails=settings.HeadDetails,bandanaText=settings.HeadDetails?settings.BandanaText:null,method=settings.Method+(settings.HeadDetails?": image-derived body, authored bandana, geometric lettering, individual teeth and rear ties on the head bone":": silhouette fitting, rigid bone hierarchy, palette projection, adaptive face refinement"),validated="Native body readback and archive readback. In-game playback not verified."},new JsonSerializerOptions{WriteIndented=true}));
        }
        File.WriteAllText(Path.Combine(run,"INSTALL.txt"),"Use a separate copy of your game installation for testing. Back up its BODY.HQR, then replace that copy's BODY.HQR with the exported archive for the SAME game. The selected body entry is replaced; it appears wherever that body is used. Restart the game. Restore the backed-up archive to undo. character.body is an unpacked native body payload, not a whole HQR archive. OBJ is a neutral-pose preview without an animation rig. Original installations were not modified. Template animations/bone ordering are retained, but appearance, deformation, collision bounds and gameplay require in-game review. Exports containing game-derived geometry/assets are for your local use; no original game assets are bundled with the application.");
        return run;
    }
    static void ExportObj(Generated g,string folder)
    {
        using var obj=new StreamWriter(Path.Combine(folder,"character.obj"));using var mtl=new StreamWriter(Path.Combine(folder,"character.mtl"));obj.WriteLine("mtllib character.mtl");
        foreach(var v in g.Body.World())obj.WriteLine(FormattableString.Invariant($"v {v.X} {v.Y} {v.Z}"));
        foreach(int c in g.Body.Faces.Select(f=>f.Colour).Distinct()){var p=g.Palette[c];mtl.WriteLine(FormattableString.Invariant($"newmtl palette{c}\nKd {p.R/255f} {p.G/255f} {p.B/255f}"));}
        foreach(var f in g.Body.Faces){obj.WriteLine($"usemtl palette{f.Colour}");obj.WriteLine("f "+string.Join(" ",f.Points.Select(p=>p+1)));}
    }
}

