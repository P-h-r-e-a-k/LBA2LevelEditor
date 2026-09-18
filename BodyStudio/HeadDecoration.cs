using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Optional, explicit art direction. These are native head-bone polygons, not a
// preview-only decal or an OCR guess. A compact pixel font keeps LBA1 viable.
public static class HeadDecoration
{
    static readonly Dictionary<char,string[]> Font=new()
    {
        ['A']=["01110","10001","10001","11111","10001","10001","10001"],
        ['B']=["11110","10001","10001","11110","10001","10001","11110"],
        ['C']=["01111","10000","10000","10000","10000","10000","01111"],
        ['D']=["11110","10001","10001","10001","10001","10001","11110"],
        ['E']=["11111","10000","10000","11110","10000","10000","11111"],
        ['F']=["11111","10000","10000","11110","10000","10000","10000"],
        ['G']=["01111","10000","10000","10111","10001","10001","01111"],
        ['H']=["10001","10001","10001","11111","10001","10001","10001"],
        ['I']=["11111","00100","00100","00100","00100","00100","11111"],
        ['J']=["00111","00010","00010","00010","10010","10010","01100"],
        ['K']=["10001","10010","10100","11000","10100","10010","10001"],
        ['L']=["10000","10000","10000","10000","10000","10000","11111"],
        ['M']=["10001","11011","10101","10101","10001","10001","10001"],
        ['N']=["10001","11001","10101","10011","10001","10001","10001"],
        ['O']=["01110","10001","10001","10001","10001","10001","01110"],
        ['P']=["11110","10001","10001","11110","10000","10000","10000"],
        ['Q']=["01110","10001","10001","10001","10101","10010","01101"],
        ['R']=["11110","10001","10001","11110","10100","10010","10001"],
        ['S']=["01111","10000","10000","01110","00001","00001","11110"],
        ['T']=["11111","00100","00100","00100","00100","00100","00100"],
        ['U']=["10001","10001","10001","10001","10001","10001","01110"],
        ['V']=["10001","10001","10001","10001","10001","01010","00100"],
        ['W']=["10001","10001","10001","10101","10101","11011","10001"],
        ['X']=["10001","10001","01010","00100","01010","10001","10001"],
        ['Y']=["10001","10001","01010","00100","00100","00100","00100"],
        ['Z']=["11111","00001","00010","00100","01000","10000","11111"],
        ['0']=["01110","10001","10011","10101","11001","10001","01110"],
        ['1']=["00100","01100","00100","00100","00100","00100","01110"],
        ['2']=["01110","10001","00001","00010","00100","01000","11111"],
        ['3']=["11110","00001","00001","01110","00001","00001","11110"],
        ['4']=["00010","00110","01010","10010","11111","00010","00010"],
        ['5']=["11111","10000","10000","11110","00001","00001","11110"],
        ['6']=["01110","10000","10000","11110","10001","10001","01110"],
        ['7']=["11111","00001","00010","00100","01000","01000","01000"],
        ['8']=["01110","10001","10001","01110","10001","10001","01110"],
        ['9']=["01110","10001","10001","01111","00001","00001","01110"],
        ['a']=["00000","00000","01110","00001","01111","10001","01111"],
        ['e']=["00000","00000","01110","10001","11111","10000","01111"],
        ['h']=["10000","10000","10110","11001","10001","10001","10001"],
        ['k']=["10000","10000","10010","10100","11000","10100","10010"],
        ['r']=["00000","00000","10110","11001","10000","10000","10000"],
        ['-']=["00000","00000","00000","11111","00000","00000","00000"],
        [' ']=["00000","00000","00000","00000","00000","00000","00000"]
    };
    public static void ValidateText(string? text)
    {
        if(text==null||text.Length>8||text.Any(c=>!Font.ContainsKey(c)&&!Font.ContainsKey(char.ToUpperInvariant(c))))
            throw new InvalidDataException("Bandana text supports up to 8 Latin letters, digits, spaces or hyphens. Short labels are more legible.");
    }
    public static void Add(List<Vector3> points,Action<int[],int> face,float height,Settings settings)
    {
        ValidateText(settings.BandanaText);
        float h=height,r=h*.055f*(settings.HeadScale/.7f),rz=r*.96f;
        var cache=new Dictionary<Vector3,int>();
        for(int i=0;i<points.Count;i++)cache.TryAdd(points[i],i);
        int Point(Vector3 p)
        {
            if(!settings.NegativeZFront)p=new(-p.X,p.Y,-p.Z);
            if(cache.TryGetValue(p,out int index))return index;
            int result=points.Count;points.Add(p);cache[p]=result;return result;
        }
        void Poly(int tone,params Vector3[] p)=>face(p.Select(Point).ToArray(),tone);
        Vector3 BandPoint(int j,bool top)
        {
            float a=j*MathF.Tau/12,z=rz*MathF.Sin(a);
            if(MathF.Sin(a)<-.49f)z=-rz;
            return new(r*1.025f*MathF.Cos(a),(top?.966f:.927f)*h,z);
        }
        for(int j=0;j<12;j++)
            if(j<7||j>10)Poly(j<6?230:255,BandPoint(j,true),BandPoint((j+1)%12,true),BandPoint((j+1)%12,false),BandPoint(j,false));

        // The front band is tiled with BOTH white and black faces. No white face
        // lies behind a letter, avoiding painter-order occlusion in the actual LBA1 engine.
        string text=settings.BandanaText;
        int columns=Math.Max(1,text.Length*6-1),gridWidth=columns+2;
        float textWidth=r*1.65f,cellX=textWidth/columns,cellY=.024f*h/7;
        float left=-textWidth/2,topY=.958f*h;
        float X(int x)=>x==0?BandPoint(7,true).X:x==gridWidth?BandPoint(11,true).X:left+(x-1)*cellX;
        float Y(int y)=>y==0?.966f*h:y==9?.927f*h:topY-(y-1)*cellY;
        bool[,] black=new bool[gridWidth,9],used=new bool[gridWidth,9];
        for(int c=0;c<text.Length;c++)
        {
            string[] glyph=Font.TryGetValue(text[c],out var f)?f:Font[char.ToUpperInvariant(text[c])];
            for(int y=0;y<7;y++)for(int x=0;x<5;x++)black[c*6+x+1,y+1]=glyph[y][x]=='1';
        }
        for(int y=0;y<9;y++)for(int x=0;x<gridWidth;x++)
        {
            if(used[x,y])continue;bool on=black[x,y];
            int width=1;while(x+width<gridWidth&&!used[x+width,y]&&black[x+width,y]==on)width++;
            int rows=1;while(y+rows<9&&Enumerable.Range(x,width).All(xx=>!used[xx,y+rows]&&black[xx,y+rows]==on))rows++;
            for(int yy=y;yy<y+rows;yy++)for(int xx=x;xx<x+width;xx++)used[xx,yy]=true;
            Poly(on?0:255,new(X(x),Y(y),-rz),new(X(x+width),Y(y),-rz),new(X(x+width),Y(y+rows),-rz),new(X(x),Y(y+rows),-rz));
        }
        // Two rows of eight separate teeth. Black gaps remain actual gaps between white faces.
        for(int row=0;row<2;row++)for(int t=0;t<8;t++)
        {
            float n=(t-3.5f)/3.5f,cx=n*r*.59f,half=r*.065f;
            float top=(row==0?.910f:.887f)*h-n*n*(row==0?.005f:-.003f)*h;
            float bottom=(row==0?.891f:.874f)*h+n*n*(row==0?.002f:.008f)*h;
            float z=-r*.90f*MathF.Sqrt(1-MathF.Pow(cx/r,2))-3;
            Poly(255,new(cx-half*.8f,top,z),new(cx+half*.8f,top,z),new(cx+half,bottom,z),new(cx-half,bottom,z));
        }
        // Rear knot, then two double-sided cloth tails, all owned by the head bone.
        Vector3 centre=new(0,.943f*h,rz+7);
        Vector3[] knot=[new(-r*.17f,.946f*h,rz+3),new(0,.957f*h,rz+3),new(r*.17f,.946f*h,rz+3),new(0,.933f*h,rz+3)];
        for(int j=0;j<4;j++)Poly(j%2==0?255:195,centre,knot[(j+1)%4],knot[j]);
        void Tail(Vector3 a,Vector3 b,Vector3 c,Vector3 d)
        {Poly(240,d,c,b,a);Poly(240,a,b,c,d);}
        Tail(new(-r*.11f,.939f*h,rz+2),new(r*.025f,.939f*h,rz+2),new(-r*.11f,.853f*h,rz+14),new(-r*.34f,.863f*h,rz+13));
        Tail(new(0,.939f*h,rz+3),new(r*.15f,.939f*h,rz+3),new(r*.44f,.881f*h,rz+11),new(r*.23f,.883f*h,rz+13));
    }
}
