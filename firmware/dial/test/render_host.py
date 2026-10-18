"""Render the firmware UI with the installed Arduino_GFX implementation, without device APIs."""
from pathlib import Path
import argparse, ast, hashlib, json, os, re, struct, subprocess, zlib

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[3])
parser.add_argument('--output',type=Path,default=Path(__file__).resolve().parents[3]/'artifacts/dial-development/display-host')
parser.add_argument('--baseline',type=Path,default=None)
parser.add_argument('--baseline-render-output',type=Path,default=None,
                    help='Prior actual-render output to verify retained values/RPM/temperatures, matching --baseline hash')
parser.add_argument('--pixel-baseline-output',type=Path,default=None,
                    help='Prior reviewed output whose complete frame pixels must remain identical')
parser.add_argument('--arc-baseline',type=Path,default=None,
                    help='Prior firmware source for exhaustive 0..100 percent arc raster comparison')
parser.add_argument('--layout-baseline-output',type=Path,default=None,
                    help='Prior output for an approved telemetry layout change; percent, identity, arc and legacy pixels must match')
options=parser.parse_args()
root = options.repo
out = options.output
out.mkdir(parents=True, exist_ok=True)
lib = root / 'firmware/dial/.pio/libdeps/jc3636k518/GFX Library for Arduino/src'
stub = out / 'stubs'
stub.mkdir(exist_ok=True)
(stub/'Arduino.h').write_text('''#pragma once
#include <cstdint>
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <algorithm>
#include <string>
#define PROGMEM
#define PI 3.14159265358979323846
using String = std::string;
struct __FlashStringHelper;
template<class T> T constrain(T x,T a,T b){return std::min(std::max(x,a),b);}
using std::min; using std::max;
''', encoding='utf-8')
(stub/'Print.h').write_text('''#pragma once
#include <cstddef>
#include <cstdint>
class Print { public: virtual size_t write(uint8_t)=0; size_t print(const char* s){size_t n=0;while(*s)n+=write(static_cast<uint8_t>(*s++));return n;} };
''', encoding='utf-8')
(stub/'U8g2lib.h').write_text('''#pragma once
#include <cstdint>
#define U8G2_FONT_SECTION(x)
#define U8G2_WITH_UNICODE
extern const uint8_t u8g2_font_unifont_t_japanese3[];
''', encoding='utf-8')
font_source = (root/'firmware/dial/.pio/libdeps/jc3636k518/U8g2/src/clib/u8g2_fonts.c').read_text(encoding='latin1')
font = re.search(r'const uint8_t u8g2_font_unifont_t_japanese3\[149411\].*?=\s*((?:"(?:\\.|[^"\\])*"\s*)+);', font_source, re.S).group(1)
font_data = b''.join(ast.literal_eval(s).encode('latin1') for s in re.findall(r'"(?:\\.|[^"\\])*"', font))
assert len(font_data) in (149410,149411), len(font_data)
font_data = font_data.ljust(149411, b'\0')
(out/'japanese_font.cpp').write_text('#include <cstdint>\nextern const uint8_t u8g2_font_unifont_t_japanese3[]={'+','.join(map(str,font_data))+'};\n')

source_path = root/'firmware/dial/src/main.cpp'
source = source_path.read_text(encoding='utf-8')
draw_source = source[source.index('void japaneseFont()'):source.index('} // namespace')]
constants = '\n'.join(line for line in source.splitlines() if line.startswith('constexpr uint16_t '))
def cpp_function(text, signature):
    start = text.index(signature)
    begin = text.index('{', start)
    depth = 1
    end = begin + 1
    while depth:
        if text[end] == '{': depth += 1
        if text[end] == '}': depth -= 1
        end += 1
    return text[start:end]

driver_paths = [lib / 'Arduino_TFT.cpp', lib / 'display/Arduino_ST77916.cpp',
                lib / 'display/Arduino_ST77916.h']
tft, st77916, st_header = [p.read_text(encoding='utf-8') for p in driver_paths]
driver_constants = '\n'.join(line for line in st_header.splitlines()
    if re.match(r'#define ST77916_(MADCTL|CASET|RASET|RAMWR)\b|#define ST77916_MADCTL_', line))
driver_functions = '\n'.join([
    cpp_function(tft, 'void Arduino_TFT::setRotation('),
    cpp_function(tft, 'void Arduino_TFT::draw16bitRGBBitmap(\n    int16_t x, int16_t y,\n    uint16_t *bitmap'),
    cpp_function(st77916, 'void Arduino_ST77916::setRotation('),
    cpp_function(st77916, 'void Arduino_ST77916::writeAddrWindow(')])
(out / 'actual_driver_host.h').write_text(driver_constants + '\n' +
    (Path(__file__).with_name('display_driver_host.h')).read_text(encoding='utf-8') + '\n' +
    driver_functions, encoding='utf-8')
prefix = r'''
#include <fstream>
#include <iostream>
#include <vector>
#include <limits>
#include "Arduino_GFX.h"
#include "Arduino_GFX.cpp"
#include "japanese_font.cpp"
#include "board_config.h"
#include "dial_core.h"
#include "display_diff.h"
#include "display_motion.h"
#include "display_feedback.h"
#include "actual_driver_host.h"
uint32_t millis(){return 0;}
class PanelStub {
 public:
  DriverBus bus;
  Arduino_ST77916 driver{&bus};
  PanelStub(){driver.setRotation(Board::LcdRotation);}
  std::vector<uint16_t> pixels=std::vector<uint16_t>(360*360,0xFFFF);
  uint64_t transferred=0, calls=0;
  struct Transfer{int x,y,w,h;};
  std::vector<Transfer> bands;
  void draw16bitRGBBitmap(int x,int y,uint16_t* data,int w,int h){
   if(x<0 || w<1 || x+w>360 || y<0 || h<1 || y+h>360){std::cerr<<"bad transfer bounds";std::exit(3);}
   for(int row=0;row<h;++row)std::copy(data+row*w,data+(row+1)*w,pixels.begin()+(y+row)*360+x);
   driver.draw16bitRGBBitmap(x,y,data,w,h);
   transferred+=w*h;++calls;bands.push_back({x,y,w,h});
  }
  bool rotationMatches()const{
    if(bus.madctl!=(ST77916_MADCTL_MX|ST77916_MADCTL_MY))return false;
    for(int i=0;i<360*360;++i)if(bus.physical[360*360-1-i]!=pixels[i])return false;
    return true;
  }
};
PanelStub panel;
struct TextRecord { std::string text; int x,y,w,h,overlap,outside; };
class Framebuffer : public Arduino_GFX {
 public:
  std::vector<uint16_t> pixels = std::vector<uint16_t>(360*360);
  std::vector<TextRecord> texts;
  bool recording=false; int overlap=0,outside=0;
  uint64_t writes=0; int minX=360,minY=360,maxX=-1,maxY=-1;
  Framebuffer():Arduino_GFX(360,360){}
  bool begin(int32_t = -1) override { return true; }
  void writePixelPreclipped(int16_t x,int16_t y,uint16_t c) override {
   if(x<0 || y<0 || x>=360 || y>=360) return;
   ++writes;minX=std::min(minX,int(x));minY=std::min(minY,int(y));maxX=std::max(maxX,int(x));maxY=std::max(maxY,int(y));
   if(recording){ if(pixels[y*360+x]!=RGB565(13,18,27))++overlap;
     if((x-180)*(x-180)+(y-180)*(y-180)>179*179)++outside; }
   pixels[y*360+x]=c;
  }
  size_t print(const char* s){int16_t x,y;uint16_t w,h;getTextBounds(s,getCursorX(),getCursorY(),&x,&y,&w,&h);
    overlap=outside=0;recording=true;auto n=Print::print(s);recording=false;texts.push_back({s,x,y,w,h,overlap,outside});return n;}
  uint16_t* getFramebuffer(){return pixels.data();}
  void flush(){panel.draw16bitRGBBitmap(0,0,pixels.data(),360,360);}
  void resetMetrics(){writes=0;minX=minY=360;maxX=maxY=-1;texts.clear();panel.bands.clear();}
  void save(const char* name){
    std::ofstream f(std::string(name)+".ppm",std::ios::binary);f<<"P6\n360 360\n255\n";
    for(int y=0;y<360;y++)for(int x=0;x<360;x++){uint16_t c=pixels[y*360+x]; if((x-180)*(x-180)+(y-180)*(y-180)>179*179)c=0;
      char rgb[3]={char(((c>>11)&31)*255/31),char(((c>>5)&63)*255/63),char((c&31)*255/31)};f.write(rgb,3);}
    if(panel.pixels!=pixels){std::cerr<<"partial/full mismatch: "<<name;std::exit(4);}
    if(!panel.rotationMatches()){std::cerr<<"rotation mismatch: "<<name;std::exit(4);}
    JsonDocument d;d["case"]=name;d["partialMatchesFull"]=true;d["cumulativeTransferredPixels"]=panel.transferred;d["cumulativeTransferCalls"]=panel.calls;auto a=d["text"].to<JsonArray>();for(const auto&t:texts){auto r=a.add<JsonObject>();r["text"]=t.text;r["x"]=t.x;r["y"]=t.y;r["w"]=t.w;r["h"]=t.h;r["paintedOverExistingPixels"]=t.overlap;r["outsideRoundPanelPixels"]=t.outside;}
    d["writeCalls"]=writes;auto bounds=d["writtenBounds"].to<JsonArray>();bounds.add(minX);bounds.add(minY);bounds.add(maxX);bounds.add(maxY);auto bands=d["transferBands"].to<JsonArray>();for(const auto& b:panel.bands){auto band=bands.add<JsonArray>();band.add(b.x);band.add(b.y);band.add(b.w);band.add(b.h);}
    std::string s;serializeJson(d,s);std::cout<<s<<'\n';
  }
};
Framebuffer canvas;
Dial::Session session;
Dial::Display::Motion iconMotion;
Dial::Display::Feedback feedback;
bool drawnFeedback=false;
bool displayReady=true,touchReady=true,needsRender=true,lastFresh=false;
uint32_t messagePage=0,lastRender=0,textPageStartedAt=0;
Dial::State drawnState;
bool drawnPreview=false,drawnInFlight=false,drawnQueued=false;
int32_t drawnDesired=0;
std::vector<uint16_t> previousStorage(360*360);
uint16_t* previousFramebuffer=previousStorage.data();
bool previousFramebufferValid=false;
uint32_t maxDrawMs=0,maxFlushMs=0,lastFlushPixels=0,renderCount=0;
uint32_t animationFrameCount=0,maxAnimationDrawMs=0,maxAnimationFlushMs=0,maxAnimationRenderMs=0,lastAnimationFlushPixels=0;
'''
suffix = (Path(__file__).resolve().parent/'display_host.cpp').read_text(encoding='utf-8')
if 'void prepareArcGaps()' in source:
    suffix = suffix.replace('int main() {', '''int main() {
    prepareArcGaps();
    if(!arcGapsReady || arcGapCount==0 || arcGapCount>ArcGapCapacity){std::cerr<<"invalid arc cache";std::exit(51);}
    unsigned gapPixels=0;
    for(unsigned i=0;i<arcGapCount;++i){const auto& gap=arcGaps[i];
        if(!gap.width || gap.x+gap.width>360 || gap.y>=360){std::cerr<<"invalid gap bounds";std::exit(51);}
        gapPixels+=gap.width;
    }
    const auto beforeCache=canvas.pixels; const auto beforeCount=arcGapCount;
    canvas.resetMetrics();prepareArcGaps();
    if(canvas.pixels!=beforeCache || canvas.writes || arcGapCount!=beforeCount){std::cerr<<"arc cache is not idempotent";std::exit(51);}
    std::ofstream("arc-cache-result.json") << "{\\"ready\\":true,\\"spanCount\\":" << arcGapCount
        << ",\\"capacity\\":" << ArcGapCapacity << ",\\"gapPixels\\":" << gapPixels
        << ",\\"storageBytes\\":" << sizeof(arcGaps) << ",\\"preparationIdempotent\\":true}";
''')
if options.arc_baseline:
    reference = cpp_function(options.arc_baseline.read_text(encoding='utf-8'), 'void meterArc(')
    reference = reference.replace('void meterArc(', 'void referenceMeterArc(', 1)
    sweep = r'''
void verifyArcRaster() {
    JsonDocument report; auto differences=report["differences"].to<JsonArray>();
    for (bool dashed : {false, true}) for (int value = 0; value <= 100; ++value) {
        canvas.fillScreen(Background);
        meterArc(405.0f, Track, dashed);
        if(value) meterArc(135.0f+value*2.7f, FanAccent, dashed);
        const auto actual = canvas.pixels;
        canvas.fillScreen(Background);
        referenceMeterArc(405.0f, Track, dashed);
        if(value) referenceMeterArc(135.0f+value*2.7f, FanAccent, dashed);
        if(actual != canvas.pixels) {
            size_t changed=0;for(size_t i=0;i<actual.size();++i)if(actual[i]!=canvas.pixels[i])++changed;
            auto difference=differences.add<JsonObject>();difference["dashed"]=dashed;difference["value"]=value;difference["pixels"]=changed;
        }
    }
    report["passed"]=differences.size()==0;report["states"]=202;report["pixelsPerState"]=129600;
    std::string json;serializeJson(report,json);std::ofstream("arc-raster-result.json")<<json;
    if(differences.size()){std::cerr<<json;std::exit(50);}
}
'''
    suffix = reference + '\n' + sweep + '\n' + suffix
    suffix = suffix.replace('readableTelemetryCases(); }', 'readableTelemetryCases(); verifyArcRaster(); }')
(out/'ui_host.cpp').write_text(prefix+constants+'\n'+draw_source+suffix, encoding='utf-8')
vc=root/'.tools/msvc-14.43-stable/VC/Tools/MSVC/14.43.34808'
sdk=Path('C:/Program Files (x86)/Windows Kits/10')
version='10.0.26100.0'
env=os.environ.copy()
for key in ('CL','_CL_','LINK','_LINK_'):env.pop(key,None)
env['VSLANG']='1033'
env['INCLUDE']=';'.join(map(str,[vc/'include',*[sdk/'Include'/version/p for p in ('ucrt','shared','um')]]))
env['LIB']=';'.join(map(str,[vc/'lib/x64',*[sdk/'Lib'/version/p/'x64' for p in ('ucrt','um')]]))
env['PATH']=str(vc/'bin/Hostx64/x64')+os.pathsep+env.get('PATH','')
args=[str(vc/'bin/Hostx64/x64/cl.exe'),'/nologo','/std:c++17','/EHsc','/utf-8','/J','/DLITTLE_FOOT_PRINT','/W0',
      '/I'+str(stub),'/I'+str(lib),'/I'+str(root/'firmware/dial/src'),'/I'+str(root/'firmware/dial/.pio/libdeps/jc3636k518/ArduinoJson/src'),
      str(out/'ui_host.cpp'),'/Fe:'+str(out/'ui_host.exe'),'/Fo:'+str(out)+'\\']
build=subprocess.run(args,cwd=out,env=env,text=True,encoding='utf-8',errors='replace',capture_output=True)
(out/'build.log').write_text(build.stdout+build.stderr,encoding='utf-8')
if build.returncode:print(build.stdout+build.stderr);raise SystemExit(build.returncode)
run=subprocess.run([str(out/'ui_host.exe')],cwd=out,env=env,text=True,encoding='utf-8',errors='replace',capture_output=True)
(out/'text-bounds.jsonl').write_text(run.stdout,encoding='utf-8')
if run.returncode:print(run.stderr);raise SystemExit(run.returncode)
def png(path,w,h,pixels):
 def chunk(t,d):return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
 raw=b''.join(b'\0'+pixels[y*w*3:(y+1)*w*3] for y in range(h))
 path.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b''))

images = {}
for p in sorted(out.glob('*.ppm')):
    pixels = p.read_bytes().split(b'\n', 3)[3]
    png(p.with_suffix('.png'), 360, 360, pixels)
    images[p.stem] = pixels
records = [json.loads(line) for line in run.stdout.splitlines()]
by_case = {r['case']: r for r in records}
issues = [{'case': r['case'], 'text': t} for r in records for t in r['text']
          if t['paintedOverExistingPixels'] or t['outsideRoundPanelPixels']]

def texts(case):
    return [t['text'] for t in by_case[case]['text']]

def region(case, x, y, w, h):
    pixels = images[case]
    return b''.join(pixels[(row * 360 + x) * 3:(row * 360 + x + w) * 3]
                    for row in range(y, y + h))

def rgb565(r, g, b):
    return bytes(((r >> 3) * 255 // 31, (g >> 2) * 255 // 63, (b >> 3) * 255 // 31))

fan = rgb565(163, 217, 54)
pump = rgb565(77, 184, 255)
white = rgb565(234, 239, 247)

def color_count(data, color):
    return sum(data[i:i + 3] == color for i in range(0, len(data), 3))

def arc_count(case, color):
    pixels = images[case]
    return sum(pixels[(y * 360 + x) * 3:(y * 360 + x + 1) * 3] == color
               for y in range(360) for x in range(360)
               if (y <= 180 or abs(x - 180) >= y - 180)
               and 146 ** 2 <= (x - 180) ** 2 + (y - 180) ** 2 <= 164 ** 2)

neutral = ['unknown-fan-label', 'similar-fan-id', 'lighting-fan-id',
           'lighting-pump-id', 'no-target', 'fan-stale', 'pump-stale']
checks = {
    'allTextInsideCircleWithoutOverpainting': not issues,
    'allPartialTransfersEqualFullFramebuffer': all(r['partialMatchesFull'] for r in records),
    'fanTitleAndCount': '全ファン 3台' in texts('fan-60'),
    'pumpTitleAndCount': 'ポンプ 1台' in texts('pump-60'),
    'memberCount64Readable': '全ファン 64台' in texts('fan-64') and 'ポンプ 64台' in texts('pump-64'),
    'genericFallbackKeepsHostLabel': '全ファン・3台' in texts('unknown-fan-label'),
    'noModeColorForUnknownLightingMissingOrExpiredState': all(
        color_count(images[c], fan) == 0 and color_count(images[c], pump) == 0 for c in neutral),
    'iconsAreDifferent': region('fan-60', 162, 43, 37, 37) != region('pump-60', 162, 43, 37, 37),
    'mixedIsNotShownAsConfirmedCommonValue': '混在' in texts('fan-mixed') and '80' not in texts('fan-mixed'),
    'mixedPreviewShowsLatestDesiredValue': '90' in texts('fan-mixed-preview') and '混在' not in texts('fan-mixed-preview'),
    'errorUsesShortStatus': '操作不可' in texts('fan-disabled') and not any('してください' in t for t in texts('fan-disabled')),
    'expiredClearsPriorValueAndTelemetry': all('--' in texts(c) and not any('RPM' in t for t in texts(c))
                                               and 'CPU' not in texts(c) for c in ['fan-stale', 'pump-stale']),
    'longFallbackLabelStillFullyPages':
        ''.join(t['text'] for p in range(3) for t in by_case[f'unknown-long-page-{p}']['text'] if t['y'] in (73, 91)) == '界' * 64,
    'hostExplanationsNeverLeakToRecognizedMode': all(
        '操作不可' in texts(f'fan-error-page-{p}') and not any('語' in t for t in texts(f'fan-error-page-{p}')) for p in range(4)),
    'normalModeHasOnlyIdentityValueTelemetryAndScale': all(
        set(texts(mode+'-60')) == {title, '60', '%', rpm, 'CPU', 'GPU', '水温', '65', '76', '32.7', 'C', '0', '100'}
        for mode,title,rpm in [('fan','全ファン 3台','900-1500 RPM'),('pump','ポンプ 1台','2100 RPM')]),
    'shortErrorsRenderWithoutTruncation': all(label in texts(case) for case,label in [
        ('quiet-rejected','適用失敗'),('quiet-unverified','確認不可'),('quiet-saved','出力未確認'),
        ('quiet-touch-error','タッチ異常'),('no-target','対象なし'),('fan-stale','未接続')]),
    'latestSuccessShowsExactCountOnlyBriefly': all(
        count in texts('quiet-'+mode+'-confirmed') and count not in texts('quiet-'+mode+'-expired')
        and count not in texts(mode+'-60') for mode,count in [('fan','3/3'),('pump','1/1')]),
    'unverifiedOutputHasNoColoredArc': arc_count('quiet-busy-unverified', fan) == 0 and '--' in texts('quiet-busy-unverified'),
    'pendingArcUsesVisibleGaps': all(
        0.4 * arc_count('quiet-'+mode+'-confirmed', color) < arc_count('quiet-'+mode+'-pending', color)
        < 0.8 * arc_count('quiet-'+mode+'-confirmed', color) for mode,color in [('fan',fan),('pump',pump)]),

}
for name, color, rpm, ack in [('fan', fan, '900-1500 RPM', '確認3/3'), ('pump', pump, '2100 RPM', '確認1/1')]:
    base = name + '-60'
    checks[name + 'ColorStableForPreviewBusyDisabled'] = all(
        region(base, 162, 43, 37, 37) == region(name + '-' + s, 162, 43, 37, 37)
        and arc_count(name + '-' + s, color) > 0 for s in ['preview', 'pending', 'queued', 'busy', 'disabled'])
    checks[name + 'ValueWhiteDuringImmediatePreview'] = all(color_count(region(name + '-' + s, 90, 109, 180, 58), white) > 0
                                                              and color_count(region(name + '-' + s, 90, 109, 180, 58), color) == 0
                                                              for s in ['60', 'preview', 'pending', 'queued'])
    checks[name + 'ArcZeroToFull'] = arc_count(name + '-0', color) == 0 < arc_count(base, color) < arc_count(name + '-100', color)
    checks[name + 'RpmAndTemperaturesRetained'] = all(t in texts(base) for t in [rpm, 'CPU', 'GPU', '水温', '65', '76', '32.7', 'C'])
    checks[name + 'PreviewAndQueuedValuesRemainImmediate'] = '70' in texts(name + '-preview') and '70' in texts(name + '-pending') and '77' in texts(name + '-queued')
    checks[name + 'MissingTelemetryIsNotZero'] = '-- RPM (0/' + ('3)' if name == 'fan' else '1)') in texts(name + '-missing-telemetry') and texts(name + '-missing-telemetry').count('--') == 3
    checks[name + 'DisabledRemainsExplicit'] = any('操作不可' in t for t in texts(name + '-disabled'))

def text_records(case, text=None, y=None):
    return [t for t in by_case[case]['text'] if (text is None or t['text']==text) and (y is None or t['y']==y)]

def pixel(case,x,y):
    return images[case][(y*360+x)*3:(y*360+x+1)*3]

muted=rgb565(139,151,171)
checks['normalRpmUsesLarger24PixelCell'] = all(
    text_records(case,rpm,178)[0]['h']==24 for case,rpm in [('fan-60','900-1500 RPM'),('pump-60','2100 RPM')])
checks['fiveDigitRpmRangeRemainsLargeAndComplete'] = any(t['w']==270 and t['h']==24 and t['y']==178
    for t in text_records('telemetry-fan-wide-rpm','10000-12000 RPM'))
checks['LongestRpmRangeFitsWithoutTruncation'] = any(t['w']<=272 and t['h']==16 and t['x']>=44 and t['x']+t['w']<=316
    for t in text_records('telemetry-fan-max-range','999999-1000000 RPM'))
checks['AllNormalTemperatureValuesUse24PixelCell'] = all(t['h']==24 for t in text_records('fan-60',y=236))
checks['DecimalWater325330And1500NeverShrink'] = all(any(t['h']==24 and t['w']==18*len(value)
    for t in text_records(case,value,236)) for case,value in [('telemetry-water-32-5','32.5'),('telemetry-water-33-0','33.0'),('telemetry-fan-max-temps','150.0')])
checks['DegreeMarkAndCVisibleInEachHeading'] = all(
    texts(case).count('C')==3 and all(all(pixel(case,t['x']+t['w']+10+dx,216+dy)==muted
       for dx,dy in [(-1,0),(1,0),(0,-1),(0,1)])
       for t in by_case[case]['text'] if t['text'] in ('CPU','GPU','水温'))
    for case in ['fan-60','pump-60','telemetry-fan-max-temps'])
checks['ZeroAndMissingTemperatureRemainDistinct'] = all(
    texts('telemetry-'+mode+'-zero').count('0')>=3 and '0.0' in texts('telemetry-'+mode+'-zero')
    and texts('telemetry-'+mode+'-missing').count('--')==3 for mode in ['fan','pump'])
checks['WaterFractionPreservedAndCpuGpuRounded'] = all(
    '100' in texts('telemetry-'+mode+'-rounding') and '150' in texts('telemetry-'+mode+'-rounding')
    and '99.9' in texts('telemetry-'+mode+'-rounding') for mode in ['fan','pump'])
checks['IndependentTemperatureMissingDoesNotHideValidZeroOrFraction'] = all(t in texts('telemetry-independent-missing') for t in ['0','--','0.1'])
checks['ErrorAndSuccessRowsRemainSeparateFromTelemetry'] = all(t['y']>=278
    for case in ['quiet-fan-confirmed','quiet-pump-confirmed','telemetry-max-unverified','telemetry-max-rejected']
    for t in by_case[case]['text'] if t['text'] in ['3/3','1/1','確認不可','適用失敗'])

if options.baseline:
    baseline_source = options.baseline.read_text(encoding='utf-8')
    unchanged_functions = ['encoderPins', 'encoderInterrupt', 'sampleEncoder', 'discardInput',
                           'transmit', 'hello', 'readTouch', 'handleInput', 'setup', 'loop',
                           'displayValue', 'modeIcon', 'renderIconOnly', 'flushDisplay', 'successCheck',
                           'rasterMeterArc', 'prepareArcGaps', 'clearArcGaps', 'dashedArcEnd', 'meterArc']
    def named_function(text, name):
        signature = re.search(r'^[a-zA-Z][^\n]*\b'+name+r'\(', text, re.M).group(0)
        return cpp_function(text, signature).replace('    diagnostics["lcdRotation"] = panel.getRotation();\n', '').replace('        prepareArcGaps();\n', '')
    unchanged = {name: named_function(source, name) == named_function(baseline_source, name)
                 for name in unchanged_functions}
    checks['existingInputTransportTimingPercentIconAndFlushFunctionsUnchanged'] = all(unchanged.values())
    def receive_without_observer(text):
        text = named_function(text, 'receiveFrames')
        for line in ['        const uint32_t previousSeq = session.inFlightSeq();\n',
                     '        const bool previousPreview = session.hasPreview(), previousFresh = session.fresh(now);\n',
                     '        const bool previousQueued = session.hasQueuedDesired();\n',
                     '        const int32_t previousDesired = session.desiredValue();\n',
                     '            feedback.reset();\n']:
            text = text.replace(line, '')
        text = text.replace('if (result == Dial::FrameResult::Welcome) { iconMotion.reset(); feedback.reset(); }',
                            'if (result == Dial::FrameResult::Welcome) iconMotion.reset();')
        text = re.sub(r'                feedback.observe\(previous, previousSeq, previousPreview, previousQueued, previousDesired,\n\s+cancellation, previousFresh, session, now\);\n', '', text)
        return text
    checks['receiveControlStatementsUnchangedApartFromReadOnlyObserver'] = receive_without_observer(source) == receive_without_observer(baseline_source)
    old_board = (options.baseline.parent / 'board_config-before.h').read_text(encoding='utf-8')
    board = (root / 'firmware/dial/src/board_config.h').read_text(encoding='utf-8')
    checks['BoardWiringAndRotationUnchanged'] = re.sub(r'//[^\n]*', '', board).split() == re.sub(r'//[^\n]*', '', old_board).split()

quiet_report = json.loads((out / 'quiet-result.json').read_text(encoding='utf-8'))
checks['actualQuietFeedbackAndDriverTestsPass'] = quiet_report['passed']
readability_report = json.loads((out/'readability-result.json').read_text(encoding='utf-8'))
checks['readabilityFixturesPreserveStateAndRejectInvalidData'] = readability_report['passed']

motion_report = json.loads((out / 'motion-result.json').read_text(encoding='utf-8'))
checks['actualFrameMotionChecksPass'] = motion_report['passed']
pixel_baseline_proof = None
if options.pixel_baseline_output:
    prior = json.loads((options.pixel_baseline_output / 'result.json').read_text(encoding='utf-8'))
    if not prior['passed']: raise SystemExit('Pixel baseline must be a passing reviewed render')
    if options.arc_baseline and prior['sourceSha256'] != hashlib.sha256(options.arc_baseline.read_bytes()).hexdigest():
        raise SystemExit('Arc baseline source does not match the pixel baseline')
    changed = [name for name in prior['cases'] if name not in images or
               images[name] != (options.pixel_baseline_output / (name+'.ppm')).read_bytes().split(b'\n',3)[3]]
    checks['allPriorCompleteFramePixelsIdentical'] = not changed
    pixel_baseline_proof = {'cases':len(prior['cases']), 'changedCases':changed,
                           'sourceSha256':prior['sourceSha256'], 'path':str(options.pixel_baseline_output)}
arc_raster_proof = None
if options.arc_baseline:
    arc_raster_proof = json.loads((out/'arc-raster-result.json').read_text(encoding='utf-8'))
    arc_raster_proof['sourceSha256'] = hashlib.sha256(options.arc_baseline.read_bytes()).hexdigest()
    checks['all202ArcStatesMatchPriorRasterExactly'] = arc_raster_proof['passed']
    if (out/'arc-cache-result.json').exists():
        arc_raster_proof['cache'] = json.loads((out/'arc-cache-result.json').read_text(encoding='utf-8'))
baseline_render_proof = None
if options.baseline_render_output:
    if not options.baseline:
        raise SystemExit('--baseline-render-output requires --baseline')
    baseline_report_file = options.baseline_render_output / 'result.json'
    baseline_report = json.loads(baseline_report_file.read_text(encoding='utf-8'))
    baseline_hash = hashlib.sha256(options.baseline.read_bytes()).hexdigest()
    if not baseline_report['passed'] or baseline_report['sourceSha256'] != baseline_hash:
        raise SystemExit('Prior rendering proof does not match the supplied baseline source')
    preserved = []
    preserved_names = [mode+'-'+state for mode in ['fan','pump'] for state in
        ['0','60','100','preview','pending','queued','busy','disabled','missing-telemetry','64']]
    preserved_names += ['fan-mixed','fan-mixed-preview']
    for name in preserved_names:
        old = (options.baseline_render_output / (name + '.ppm')).read_bytes().split(b'\n', 3)[3]
        new = images[name]
        # Target, output, RPM and all three temperatures retain exactly their
        # prior pixels. The intentionally changed arc/feedback/footer are outside.
        preserved.append(all(old[(y * 360 + x) * 3:(y * 360 + x + 1) * 3] ==
                             new[(y * 360 + x) * 3:(y * 360 + x + 1) * 3]
                             for y in range(87,223) for x in range(65,296) if (x-180)**2+(y-180)**2 < 140**2))
    checks['priorIdentityValueAndTelemetryPixelsPreserved'] = all(preserved)
    baseline_render_proof = {'sourceSha256': baseline_hash, 'cases': len(preserved),
                             'reportSha256': hashlib.sha256(baseline_report_file.read_bytes()).hexdigest(),
                             'path': str(options.baseline_render_output), 'comparedCases': preserved_names}

layout_baseline_proof=None
if options.layout_baseline_output:
    prior_path=options.layout_baseline_output/'result.json'
    prior=json.loads(prior_path.read_text(encoding='utf-8'))
    if not options.baseline or not prior['passed'] or prior['sourceSha256']!=hashlib.sha256(options.baseline.read_bytes()).hexdigest():
        raise SystemExit('Layout baseline source does not match passing render proof')
    failures=[]; retained_legacy=[]
    for name in prior['cases']:
        old=(options.layout_baseline_output/(name+'.ppm')).read_bytes().split(b'\n',3)[3]; new=images[name]
        top=old[:171*360*3]==new[:171*360*3]
        arc=all(old[(y*360+x)*3:(y*360+x+1)*3]==new[(y*360+x)*3:(y*360+x+1)*3]
                for y in range(360) for x in range(360) if (x-180)**2+(y-180)**2>=146**2)
        if not top or not arc: failures.append({'case':name,'percentTitleIconSame':top,'arcSame':arc})
        if 'rpm-only' in name or 'legacy' in name or name in ['motion-temperature-layout-change','motion-pump-fast','fan-stale','pump-stale','motion-expired']:
            retained_legacy.append({'case':name,'identical':old==new})
    checks['AllPriorCentralPercentIdentityIconsAndArcsRemainPixelIdentical']=not failures
    checks['LegacyAndDisconnectedLayoutsRemainPixelIdentical']=all(v['identical'] for v in retained_legacy)
    layout_baseline_proof={'path':str(options.layout_baseline_output),'sha256':hashlib.sha256(prior_path.read_bytes()).hexdigest(),
        'sourceSha256':prior['sourceSha256'],'comparedCases':len(prior['cases']),'failures':failures,'retainedLegacy':retained_legacy}

def contact_sheet(path, names):
    width, height = 360 * 4, 360 * ((len(names) + 3) // 4)
    sheet = bytearray(width * height * 3)
    for i, name in enumerate(names):
        x, y = i % 4 * 360, i // 4 * 360
        for row in range(360):
            sheet[((y + row) * width + x) * 3:((y + row) * width + x + 360) * 3] = images[name][row * 1080:(row + 1) * 1080]
    png(path, width, height, sheet)

primary = ['fan-60', 'pump-60', 'quiet-fan-pending', 'quiet-pump-pending',
           'quiet-fan-confirmed', 'quiet-pump-confirmed', 'quiet-fan-expired', 'quiet-pump-expired',
           'fan-mixed', 'quiet-rejected', 'quiet-unverified', 'fan-stale']
contact_sheet(out / 'contact-sheet.png', primary)
readability_names=['telemetry-fan-wide-rpm','telemetry-fan-max-range','telemetry-fan-max-temps','telemetry-pump-max-temps',
 'telemetry-water-32-5','telemetry-water-33-0','telemetry-fan-zero','telemetry-independent-missing',
 'telemetry-fan-missing','telemetry-missing-rpm-64','telemetry-max-unverified','telemetry-max-rejected']
contact_sheet(out/'contact-sheet-readability.png',readability_names)
contact_sheet(out / 'contact-sheet-all.png', list(by_case))
motion_names = [f'motion-{mode}-temps-{frame}' for mode in ['fan', 'pump'] for frame in [1, 8, 16, 24]]
contact_sheet(out / 'contact-sheet-motion.png', motion_names)
report = {
    'passed': all(checks.values()), 'checks': checks, 'cases': list(by_case),
    'renderer': 'Actual firmware render functions, installed Arduino_GFX.cpp and U8g2 font; hardware replaced by framebuffer',
    'hardwareAccess': False, 'realDisplayVerified': False,
    'sourceSha256': hashlib.sha256(source_path.read_bytes()).hexdigest(),
    'motionPolicySha256': hashlib.sha256((root / 'firmware/dial/src/display_motion.h').read_bytes()).hexdigest(),
    'testSourceSha256': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [Path(__file__), Path(__file__).with_name('display_host.cpp'), Path(__file__).with_name('display_driver_host.h')]},
    'motion': motion_report, 'quiet': quiet_report, 'readability': readability_report,
    'driverSourceSha256': {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in driver_paths},
    'unchangedFunctions': unchanged if options.baseline else None,
    'baselineRenderProof': baseline_render_proof,
    'pixelBaselineProof': pixel_baseline_proof, 'arcRasterProof': arc_raster_proof, 'layoutBaselineProof':layout_baseline_proof,
    'baselineSourceSha256': hashlib.sha256(options.baseline.read_bytes()).hexdigest() if options.baseline else None,
    'fontSha256': hashlib.sha256(font_data).hexdigest(),
    'potentialOverlapOrClipping': issues, 'contactSheetCaseOrder': primary,
    'contactSheet': str(out / 'contact-sheet.png'),
    'motionContactSheet': str(out / 'contact-sheet-motion.png'),
    'motionContactSheetCaseOrder': motion_names, 'readabilityContactSheet':str(out/'contact-sheet-readability.png'), 'readabilityContactSheetCaseOrder':readability_names,
}
(out / 'result.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'passed': report['passed'], 'checks': checks, 'cases': len(records),
                  'contactSheet': report['contactSheet']}, ensure_ascii=True))
raise SystemExit(0 if report['passed'] else 1)
