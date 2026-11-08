import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import btools.codec.*;
import btools.expressions.*;
import btools.mapaccess.*;

/** Read-only display adapter for the bundled BRouter codec. Never invokes a routing profile. */
public final class OsmRoadReader {
  private final File directory;
  private final BExpressionMetaData meta=new BExpressionMetaData();
  private final BExpressionContextWay tags=new BExpressionContextWay(meta);
  private final GeometryDecoder geometry=new GeometryDecoder();
  private final DataBuffers buffers=new DataBuffers();
  private static final Set<String> KEYS=new HashSet<>(Arrays.asList("highway","tracktype","surface","sac_scale","trail_visibility","bridge","tunnel","area","layer","access","foot","bicycle","cycleway","cycleway:right","cycleway:left","route","network","mtbnetwork","toll","embankment","ford","oneway"));
  OsmRoadReader(File directory,File lookup){this.directory=directory;meta.readMetaData(lookup);tags.setAllTagsUsed();}
  static String quote(String s){return "\""+s.replace("\\","\\\\").replace("\"","\\\"").replace("\n"," ").replace("\r"," ")+"\"";}
  static String coordinate(int lon,int lat){return "["+((lon-180000000)/1000000d)+","+((lat-90000000)/1000000d)+"]";}
  String chunk(int chunkX,int chunkY) throws Exception {
    if(chunkX<0||chunkX>=5760||chunkY<0||chunkY>=2880)throw new IllegalArgumentException("区域编号无效");
    Map<String,PhysicalFile> physical=new HashMap<>();Map<String,OsmFile> squares=new HashMap<>();
    Set<String> seen=new HashSet<>();StringBuilder output=new StringBuilder("{\"type\":\"FeatureCollection\",\"features\":[");boolean comma=false;
    final int west=chunkX*62500,south=chunkY*62500,east=west+62500,north=south+62500;
    try {
      // Include a microcell halo for links crossing the display boundary; original geometry is retained.
      for(int lat=Math.max(0,(south-31250)/1000000);lat<=Math.min(179,(north+31250)/1000000);lat++)
      for(int lon=Math.max(0,(west-31250)/1000000);lon<=Math.min(359,(east+31250)/1000000);lon++){
        int actualLon=lon/5*5-180,actualLat=lat/5*5-90;
        String filename=(actualLon<0?"W":"E")+Math.abs(actualLon)+"_"+(actualLat<0?"S":"N")+Math.abs(actualLat)+".rd5";
        File file=new File(directory,filename);if(!file.isFile())continue;
        PhysicalFile pf=physical.get(filename);
        if(pf==null){pf=new PhysicalFile(file,buffers,meta.lookupVersion,meta.lookupMinorVersion);physical.put(filename,pf);}
        if(pf.divisor<1||pf.divisor>256||1000000%pf.divisor!=0)throw new IOException("不支持的路由数据网格版本");
        String degree=lon+"/"+lat;OsmFile square=squares.get(degree);
        if(square==null){square=new OsmFile(pf,lon,lat,buffers);squares.put(degree,square);}
        if(!square.hasData())continue;
        int cell=1000000/pf.divisor;
        for(int cy=Math.max(lat*pf.divisor,south/cell-1);cy<=Math.min((lat+1)*pf.divisor-1,(north-1)/cell+1);cy++)
        for(int cx=Math.max(lon*pf.divisor,west/cell-1);cx<=Math.min((lon+1)*pf.divisor-1,(east-1)/cell+1);cx++){
        MicroCache mc=square.createMicroCache(cx,cy,buffers,null,null,true,null);
        long[] ids=new long[mc.getSize()];for(int i=0;i<ids.length;i++)ids[i]=mc.getIdForIndex(i);
        for(long id:ids){
          if(!mc.getAndClear(id))continue;
          OsmNode node=new OsmNode(id);OsmNodesMap nodes=new OsmNodesMap();nodes.put(node);node.parseNodeBody(mc,nodes,tags);
          for(OsmLink link=node.firstlink;link!=null;link=link.getNext(node)){
            if(link.isReverse(node)||link.descriptionBitmap==null)continue;
            List<String> list=tags.getKeyValueList(false,link.descriptionBitmap);Map<String,String> attributes=new LinkedHashMap<>();
            for(int i=0;i+1<list.size();i+=2)if(KEYS.contains(list.get(i))&&!list.get(i+1).equals("unknown"))attributes.put(list.get(i),list.get(i+1));
            if(!attributes.containsKey("highway")&&!"ferry".equals(attributes.get("route")))continue;
            OsmNode target=link.getTarget(node);
            String edge=node.ilon+"/"+node.ilat+"/"+target.ilon+"/"+target.ilat+"/"+Arrays.hashCode(link.geometry)+"/"+Arrays.hashCode(link.descriptionBitmap);
            if(!seen.add(edge))continue;
            int minX=Math.min(node.ilon,target.ilon),maxX=Math.max(node.ilon,target.ilon),minY=Math.min(node.ilat,target.ilat),maxY=Math.max(node.ilat,target.ilat);
            StringBuilder points=new StringBuilder(coordinate(node.ilon,node.ilat));
            if(link.geometry!=null)for(OsmTransferNode p=geometry.decodeGeometry(link.geometry,node,target,false);p!=null;p=p.next){
              points.append(',').append(coordinate(p.ilon,p.ilat));minX=Math.min(minX,p.ilon);maxX=Math.max(maxX,p.ilon);minY=Math.min(minY,p.ilat);maxY=Math.max(maxY,p.ilat);
            }
            if(maxX<west||minX>east||maxY<south||minY>north)continue;
            points.append(',').append(coordinate(target.ilon,target.ilat));
            if(comma)output.append(',');comma=true;
            output.append("{\"type\":\"Feature\",\"properties\":{\"edge\":").append(quote(edge));
            for(Map.Entry<String,String> property:attributes.entrySet())output.append(',').append(quote(property.getKey())).append(':').append(quote(property.getValue()));
            output.append("},\"geometry\":{\"type\":\"LineString\",\"coordinates\":[").append(points).append("]}}");
          }
        }
      }
      }
      return output.append("]}").toString();
    } finally {for(PhysicalFile file:physical.values())file.close();}
  }
  public static void main(String[] args)throws Exception {
    OsmRoadReader reader=new OsmRoadReader(new File(args[0]),new File(args[1]));
    BufferedReader input=new BufferedReader(new InputStreamReader(System.in,StandardCharsets.UTF_8));
    PrintWriter output=new PrintWriter(new OutputStreamWriter(System.out,StandardCharsets.UTF_8),true);
    for(String line;(line=input.readLine())!=null;){try{
      String[] p=line.split("\\t");if(p.length!=2)throw new IllegalArgumentException("请求格式无效");
      output.println(reader.chunk(Integer.parseInt(p[0]),Integer.parseInt(p[1])));
    }catch(Exception error){output.println("ERROR\t"+error.getClass().getSimpleName()+": "+String.valueOf(error.getMessage()).replace('\n',' ').replace('\r',' '));}}
  }
}
