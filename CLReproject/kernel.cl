#define MAXLON  1.308996939f
#define MINLON -1.308996939f
#define MAXLAT  1.378810109f
#define MINLAT -1.378810109f

#define RADIUS_POLES 6356.7523f
#define RADIUS_EQUATOR 6378.1370f
#define GEO_ORBIT 42142.5833f

// (RADIUS_POLES * RADIUS_POLES) / (RADIUS_EQUATOR * RADIUS_EQUATOR)
#define PLANET_ASPECT_SQ 0.993305616


float deg2rad(float v) {
  return (v * M_PI) / 180.f;
}

float rad2deg(float v) {
  return (v * 180.f) / M_PI;
}

float2 lonlat2xyf(float satLon, float lon, float lat, int coff, float cfac, int loff, float lfac) {
  lon -= satLon;

  lon = fmin(fmax(lon, MINLON), MAXLON);
  lat = fmin(fmax(lat, MINLAT), MAXLAT);

  float psi = atan(PLANET_ASPECT_SQ * tan(lat));
  float re  = RADIUS_POLES / (sqrt( 1.f - ( 1.f - PLANET_ASPECT_SQ) * cos(psi) * cos(psi)));
  float r1 = GEO_ORBIT - re * cos(psi) * cos(lon);
  float r2 = -1.f * re * cos(psi) * sin(lon);
  float r3 = re * sin(psi);

  float rn = sqrt ( r1 * r1 + r2 * r2 + r3 * r3 );
  float x = atan(-1.f * r2 / r1);
  float y = asin(-1.f * r3 / rn);
  x = rad2deg(x);
  y = rad2deg(y);

  return (float2)(coff + ((x * cfac) / 0x10000), loff + ((y * lfac) / 0x10000));
}

float2 latlon2xyf(float lat, float lon,                                 // Coordinates
  float satLon, int coff, float cfac, int loff, float lfac,             // GeoConverter
  bool fixAspect,
  float aspectRatio
  ) {
  float2 res = lonlat2xyf(deg2rad(satLon), deg2rad(lon), deg2rad(lat), coff, cfac, loff, lfac);
  if (fixAspect) {
    res.y = res.y * aspectRatio;
  }

  return res;
}

__kernel void reproject(__read_only  image2d_t srcImg, __write_only image2d_t dstImg,
  __read_only float satelliteLongitude,
  __read_only int coff,
  __read_only float cfac,
  __read_only int loff,
  __read_only float lfac,
  __read_only uint fixAspect,
  __read_only float aspectRatio,
  global __read_only float2 *latRange,
  global __read_only float2 *lonRange,
  global __read_only float2 *coverage,
  global __read_only float2 *trim,
  global __read_only uint2 *size
  ) {
  const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | // Natural coordinates
    CLK_ADDRESS_CLAMP_TO_EDGE | //Clamp to zeros
    CLK_FILTER_LINEAR;

  int2 coord = (int2)(get_global_id(0), get_global_id(1));

  //var lat = (gc.MaxLatitude - gc.TrimLatitude) - ((y * (gc.LatitudeCoverage - gc.TrimLatitude * 2)) / output.Height);
  float lat = (latRange[0].y - trim[0].x) - ((coord.y * (coverage[0].x - trim[0].x * 2.f)) / size[0].y);
  //var lon = ((x * (gc.LongitudeCoverage - gc.TrimLongitude * 2)) / output.Width) + (gc.MinLongitude + gc.TrimLongitude);
  float lon = ((coord.x * (coverage[0].y - trim[0].y * 2.f)) / size[0].x) + (lonRange[0].x + trim[0].y);

  uint4 color = (uint4)(0,0,0,255);
  if (lat > latRange[0].y || lat < latRange[0].x || lon > lonRange[0].y || lon < lonRange[0].x) {
    // Do nothing
  } else {
    float2 xy = latlon2xyf(lat, lon, satelliteLongitude, coff, cfac, loff, lfac, fixAspect > 0, aspectRatio);
    color = read_imageui(srcImg, smp, xy);
  }
  color.w = 255;
  write_imageui(dstImg, coord, color);
}

// Assume 256x256 LUT
uchar4 Lut2DVal(global __read_only uchar4 *LUT, uint x, uint y) {
  return LUT[y * 256 + x];
}

__kernel void apply2DLUT(__read_only image2d_t i0, __read_only image2d_t i1, __write_only image2d_t output,
  global __read_only uchar4 *LUT
  ) {

  const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | // Natural coordinates
    CLK_ADDRESS_CLAMP_TO_EDGE | //Clamp to zeros
    CLK_FILTER_LINEAR;

  int2 coord = (int2)(get_global_id(0), get_global_id(1));
  uint4 c0 = read_imageui(i0, smp, coord);
  uint4 c1 = read_imageui(i1, smp, coord);

  uchar4 lutColor = Lut2DVal(LUT, c0.x, c1.x);
  uint4 color = (uint4)(lutColor.z, lutColor.y, lutColor.x, 255);

  write_imageui(output, coord, color);
}

__kernel void applyCurve(__read_only  image2d_t input, __write_only image2d_t output, global __read_only uchar *curveLUT) {
  const sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | // Natural coordinates
    CLK_ADDRESS_CLAMP_TO_EDGE | //Clamp to zeros
    CLK_FILTER_LINEAR;

  int2 coord = (int2)(get_global_id(0), get_global_id(1));
  uint4 color = read_imageui(input, smp, coord);

  color.x = curveLUT[color.x];
  color.y = curveLUT[color.y];
  color.z = curveLUT[color.z];
  color.w = curveLUT[color.w];

  write_imageui(output, coord, color);
}