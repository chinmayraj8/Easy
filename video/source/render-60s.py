import os, sys, json, math, subprocess, wave, re
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0,'/private/tmp/easy-video-deps')
import imageio_ffmpeg
ROOT=Path(__file__).resolve().parents[1]
FF=imageio_ffmpeg.get_ffmpeg_exe()
W,H,FPS=1920,1080,30
TEAL='#14b8a6'; INK='#102d35'; MUTED='#62767a'; WHITE='#f5f7f1'; GOLD='#efbb67'
fontdir='/System/Library/Fonts/Supplemental/'
def font(n,b=False,mono=False): return ImageFont.truetype(fontdir+('Andale Mono.ttf' if mono else 'Arial Bold.ttf' if b else 'Arial.ttf'),n)
F={n:font(n) for n in [18,20,22,24,26,28,30,32,36,40,44,48,56,64,72,80,110,180]}
B={n:font(n,True) for n in [22,24,28,30,32,36,40,44,48,56,64,72,80,110,180]}
scenes=json.loads((ROOT/'source/scenes.json').read_text())
def run(args): subprocess.run(args,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
def ease(x): x=max(0,min(1,x));return 1-(1-x)**3
def rr(d,xy,fill,outline=None,r=24,width=2):d.rounded_rectangle(tuple(map(int,xy)),r,fill,outline,width)
def text(d,xy,s,n=32,c=INK,b=False,anchor=None):d.text(xy,s,font=(B if b else F)[n],fill=c,anchor=anchor)
def line(d,xy,c=TEAL,w=4):d.line(xy,fill=c,width=w)
def arrow(d,x,y,xx,yy,c=TEAL):
 line(d,[(x,y),(xx,yy)],c,4);a=math.atan2(yy-y,xx-x);d.polygon([(xx,yy),(xx-16*math.cos(a-.45),yy-16*math.sin(a-.45)),(xx-16*math.cos(a+.45),yy-16*math.sin(a+.45))],fill=c)
def filecard(d,x,y,label,color=TEAL,scale=1):
 w,h=154*scale,194*scale
 rr(d,(x+7,y+10,x+w+7,y+h+10),'#0a242a' if dark else '#dce2db',r=int(18*scale))
 rr(d,(x,y,x+w,y+h),'#f7f8f2',r=int(18*scale))
 d.polygon([(x+w-45*scale,y),(x+w,y+45*scale),(x+w-45*scale,y+45*scale)],fill='#cddedb')
 rr(d,(x+16*scale,y+100*scale,x+w-16*scale,y+148*scale),color,r=8)
 text(d,(x+w/2,y+124*scale),label,28,INK,True,'mm')
 line(d,[(x+23*scale,y+169*scale),(x+w-23*scale,y+169*scale)],'#b6c8c3',3)
def pill(d,x,y,s,c=TEAL):
 width=d.textlength(s,font=F[24])+40;rr(d,(x,y,x+width,y+46),c,r=23);text(d,(x+20,y+9),s,24,INK)
def card(d,x,y,w,h,title,sub='',accent=TEAL):
 rr(d,(x,y,x+w,y+h),'#173c43' if dark else '#ffffff',outline='#295159' if dark else '#dce4df',r=24)
 rr(d,(x+26,y+28,x+34,y+h-28),accent,r=4)
 text(d,(x+60,y+38),title,32,fg,True)
 if sub:text(d,(x+60,y+91),sub,24,muted)

original=scenes
indices=[0,1,2,4,6,8,10,11]
durations=[4,8,10,11,6,9,8,4]
scenes=[dict(original[i], duration=durations[k]) for k,i in enumerate(indices)]
scenes[3]['title']='Office to PDF. PDF to Word.'
scenes[3]['labels']=['DOCX','PPTX','XLSX','PDF']
for k,s in enumerate(scenes):
 if k not in (0,7):s['tag']=f"0{k} / "+s['tag'].split(' / ')[-1]
(ROOT/'source/timeline-60s.json').write_text(json.dumps(scenes,indent=2))
# Backgrounds with soft radial teal light.
yy,xx=np.mgrid[0:H,0:W];glow=np.exp(-(((xx-1500)/850)**2+((yy-390)/620)**2))
bgs=[]
for c1,c2 in [((244,246,239),(220,240,230)),((10,28,35),(18,61,65))]:
 arr=np.stack([c1[k]+(c2[k]-c1[k])*glow for k in range(3)],axis=-1).astype('uint8');bgs.append(Image.fromarray(arr))
def render(i,t):
 global dark,fg,muted
 s=scenes[i];p=t/s['duration'];dark=s['kind'] in ['hero','office','media','route','outro'];fg=WHITE if dark else INK;muted='#9ab7b9' if dark else MUTED
 im=bgs[int(dark)].copy();d=ImageDraw.Draw(im)
 # Quiet moving decorative orbits.
 for j in range(3):
  cx=1580+22*math.sin(t*.25+j);cy=500+20*math.cos(t*.2+j);r=260+j*92
  d.ellipse((cx-r,cy-r,cx+r,cy+r),outline='#21454b' if dark else '#dce8df',width=1)
 text(d,(90,55),'easy',36,fg,True);d.ellipse((181,65,191,75),fill=TEAL)
 text(d,(1830,70),'PRODUCT SHOWCASE  /  MAC',20,muted,anchor='ra')
 reveal=ease(t/1.0);dy=int(34*(1-reveal))
 text(d,(92,155+dy),s['tag'],22,TEAL if dark else '#078577',True)
 title=s['title'];n=64 if len(title)>42 else 72
 text(d,(88,205+dy),title,n,fg,True)
 kind=s['kind']
 if kind in ['hero','outro']:
  text(d,(92,355),'File conversion,',80,fg,True);text(d,(92,452),'right where you work.',64,TEAL,True)
  for k,l in enumerate(s['labels']):
   q=ease((t-.8-k*.55)/.8);text(d,(94+int((1-q)*40),615+k*62),l,40,fg)
  cx,cy=1430,565
  d.arc((cx-220,cy-220,cx+220,cy+220),-80+t*6,200+t*6,TEAL,5)
  for k,(label,col) in enumerate([('DOCX',TEAL),('PDF',GOLD),('PNG','#97bfe9')]):
   a=-1.3+k*2.1+t*.07;x=cx+math.cos(a)*190-77;y=cy+math.sin(a)*170-97;filecard(d,x,y,label,col)
  text(d,(94,875),'A customized macOS build of Filee • MIT licensed',24,muted)
 elif kind=='donut':
  cx,cy=1240,580;r=225
  names=['PNG','WEBP','PDF','JPG','TIFF','SVG']
  select=0
  for k,l in enumerate(names):
   a=-30+k*60;fill=TEAL if k==select and t>3 else '#dbe7df'
   d.pieslice((cx-r,cy-r,cx+r,cy+r),a+2,a+58,fill=fill)
   aa=math.radians(a+30);text(d,(cx+math.cos(aa)*170,cy+math.sin(aa)*170),l,28,INK,True,'mm')
  d.ellipse((cx-108,cy-108,cx+108,cy+108),fill=WHITE);text(d,(cx,cy),'Convert',28,INK,True,'mm')
  for k,l in enumerate(s['labels']):card(d,92,367+k*135,610,110,f'0{k+1}  {l}')
  q=ease((t-1)/3);x=790+q*520;y=760-q*130
  
  if t<5:filecard(d,x-77,y-97,'JPG',GOLD,.8)
  else:filecard(d,1550,490,'PNG',TEAL,.8)
  d.polygon([(x+80,y+15),(x+80,y+55),(x+91,y+43),(x+102,y+62),(x+111,y+57),(x+100,y+38),(x+117,y+38)],fill=INK)
  if t>5:pill(d,1128,863,'photo.png  •  Converted')
  text(d,(94,900),'Illustrative workflow • Option + drag on macOS',24,muted)
 elif kind=='formats':
  ex=['JPG · PNG · WEBP','DOCX · HWP · TXT','XLSX · CSV · ODS','PPTX · PPTM','Merge · Split · Export','EPUB · MOBI','ZIP · 7Z · TAR','SRT · ASS · VTT','TTF · OTF · WOFF','JSON · YAML · XML','DWG · DXF','GLB · STL · OBJ']
  for k,l in enumerate(s['labels']):
   q=ease((t-.35-k*.07)/.7);x=92+(k%4)*438;y=350+(k//4)*178+int(30*(1-q));card(d,x,y,410,150,l,ex[k],TEAL if k%2==0 else '#9db6cd')
  text(d,(94,925),'Coverage varies by source format, destination, platform and installed engines.',24,muted)
 elif kind=='transform':
  for k,(l,sub) in enumerate(zip(s['labels'],['Share lighter images','Set your output size','Keep the pages you need','Reuse the content'])):
   card(d,92+(k%2)*868,360+(k//2)*244,830,210,l,sub)
  # Small progressing indicators inside each card
  for k in range(4):
   x=152+(k%2)*868;y=528+(k//2)*244;line(d,[(x,y),(x+680,y)],'#dbe7df',5);line(d,[(x,y),(x+680*ease((t-k*.4)/4),y)],TEAL,5)
 elif kind=='office':
  for k,l in enumerate(s['labels'][:3]):filecard(d,105+k*215,405+15*math.sin(t*.7+k),l)
  arrow(d,780,520,1020,520);rr(d,(1040,390,1410,650),'#204b51');text(d,(1225,480),'ONLYOFFICE',36,WHITE,True,'mm');text(d,(1225,540),'Local rendering',24,'#a6c3c2',anchor='mm');arrow(d,1440,520,1550,520);filecard(d,1590,417,'PDF',GOLD)
  card(d,94,743,820,136,'Font-aware conversion','Reports substitutions when detected')
  card(d,948,743,875,136,'Requires Desktop Editors','Install on your Mac, then open it once',GOLD)
 elif kind=='rebuild':
  for x in [150,1170]:rr(d,(x,350,x+540,850),'#ffffff',outline='#d1ded8')
  pill(d,180,371,'PDF');pill(d,1200,371,'DOCX')
  text(d,(188,450),'Project report',30,INK,True);text(d,(1208,450),'Project report',30,INK,True)
  for k in range(6):
   x=188; y=518+k*34;w=[385,410,330,400,370,290][k];rr(d,(x,y,x+w,y+14),'#c4d2cd',r=2)
   if t>1+k*.3:rr(d,(1208,y,1208+w,y+14),'#779a94',r=2)
   if t<5:d.rectangle((x-6,y-5,x+w+6,y+20),outline=TEAL,width=1)
  for k in range(4):line(d,[(1208+k*98,745),(1208+k*98,813)],TEAL,2)
  for k in range(3):line(d,[(1208,745+k*34),(1502,745+k*34)],TEAL,2)
  arrow(d,740,585,1110,585);text(d,(925,510),'Rebuild',36,INK,True,'mm');text(d,(925,655),'Text → structure',24,MUTED,anchor='mm')
  text(d,(94,925),'Editable content • Layout may reflow • Equations become text, not equation objects',24,muted)
 elif kind=='media':
  rr(d,(94,350,980,850),'#173c43',outline='#295159');
  for k in range(64):
   x=130+k*12;v=30+100*abs(math.sin(k*.31+t*1.6))*abs(math.cos(k*.17-t*.5));line(d,[(x,592-v),(x,592+v)],TEAL if k<40 else '#567a84',6)
  pill(d,128,382,'FFmpeg');text(d,(130,775),'CONVERT / EXTRACT / RESIZE',24,'#9ab7b9')
  for k,l in enumerate(s['labels']):card(d,1040,350+k*133,785,113,l)
  text(d,(94,925),'Optional engine required • Codec and hardware support depend on your setup',24,muted)
 elif kind=='presets':
  card(d,94,350,650,500,'WEBP • Web ready','Illustrative preset')
  for k,(l,v) in enumerate([('Quality','80'),('Width','1600 px'),('Output','Converted/'),('Name','{name}.webp')]):
   y=510+k*73;text(d,(150,y),l,28,MUTED);text(d,(684,y),v,28,INK,True,'ra')
  for k,l in enumerate(s['labels']):
   y=370+k*116;text(d,(880,y),l,30,INK,True);line(d,[(1210,y+20),(1770,y+20)],'#ccdbd4',8);q=[.8,.6,.92,.7][k]*ease((t-k*.3)/2);d.ellipse((1210+q*560-12,y+8,1210+q*560+12,y+32),fill=TEAL)
  pill(d,880,865,'Custom toolbars');pill(d,1135,865,'Light / Dark')
 elif kind=='automation':
  for k,(l,sub) in enumerate(zip(s['labels'],['Many files, one action','New arrivals → converted','Use the same engine pipeline'])):card(d,94+k*582,350,550,170,l,sub)
  for k in range(5):
   phase=(t*.15+k*.2)%1;x=180+phase*1500;y=620+22*math.sin(phase*math.pi*2);filecard(d,x,y,'PDF' if phase>.55 else 'DOCX',TEAL if phase>.55 else '#9db6cd',.65)
  rr(d,(94,855,1824,939),INK,r=15);d.text((125,879),'filee convert *.png --to webp --quality 80 -o converted',font=font(28,mono=True),fill='#94e6d4')
 elif kind=='route':
  for k,l in enumerate(s['labels']):
   x=94+k*446;card(d,x,440,390,200,l,['A source file','Find a valid path','Run the conversion','Save the result'][k]);
   if k<3:arrow(d,x+400,540,x+434,540)
  for k in range(3):
   phase=(t*.35+k/3)%1;x=120+phase*1640;d.ellipse((x-8,696,x+8,712),fill=TEAL)
  text(d,(94,786),'Example route',24,muted);text(d,(94,834),'HWP  →  HWPX  →  DOCX',44,fg,True)
  text(d,(1825,850),'Automatic steps. One request.',28,muted,anchor='ra')
 elif kind=='privacy':
  rr(d,(980,355,1780,814),INK,r=32);rr(d,(1005,380,1755,789),'#e5efe7',r=18);d.polygon([(950,830),(1810,830),(1860,860),(900,860)],fill='#77958f')
  cx,cy=1380,565;d.rounded_rectangle((cx-62,cy-74,cx+62,cy+54),40,outline=TEAL,width=12);rr(d,(cx-90,cy-6,cx+90,cy+114),TEAL,r=18);d.ellipse((cx-9,cy+35,cx+9,cy+53),fill=INK);line(d,[(cx,cy+48),(cx,cy+70)],INK,8)
  for k,l in enumerate(s['labels']):card(d,94,367+k*150,740,125,l)
  text(d,(94,925),'*After required engines are installed. Downloads require internet access.',24,muted)
 # Footer and scene progress
 line(d,[(92,1000),(1825,1000)],'#335159' if dark else '#cadbd1',2)
 for k in range(len(scenes)):
  x=94+k*(1730/len(scenes));line(d,[(x,1000),(x+(1730/len(scenes)-20)*(1 if k<i else p if k==i else 0),1000)],TEAL,4)
 text(d,(94,1020),'EASY  /  '+('Built on Filee by Myeonggu Kim and contributors' if i==len(scenes)-1 else 'Animated interface illustrations'),18,muted)
 text(d,(1825,1020),f'{i+1:02} / {len(scenes):02}',18,muted,anchor='ra')
 # Short fades from/to the background between chapters.
 alpha=min(1,t/.28,(s['duration']-t)/.28)
 if alpha<1:im=Image.blend(bgs[int(dark)],im,max(0,alpha))
 return im

# Render an exactly 60-second visual master; narration is added after Fish Audio succeeds.
with open(ROOT/'source/encode-60s.log','w') as log:
 proc=subprocess.Popen([FF,'-y','-f','rawvideo','-vcodec','rawvideo','-s',f'{W}x{H}','-pix_fmt','rgb24','-r',str(FPS),'-i','-','-an','-c:v','libx264','-preset','fast','-crf','19','-pix_fmt','yuv420p','-threads','6',str(ROOT/'source/picture-60s.mp4')],stdin=subprocess.PIPE,stderr=log)
 for i,s in enumerate(scenes):
  render(i,min(5,s['duration']/2)).save(ROOT/f'preview/short-{i+1:02}.jpg',quality=92)
  for f in range(round(s['duration']*FPS)):proc.stdin.write(render(i,f/FPS).tobytes())
  print('Rendered short scene',i+1,'/',len(scenes),flush=True)
 proc.stdin.close()
 if proc.wait():raise RuntimeError('Video encode failed')
sr=48000;total=60;t=np.arange(sr*total)/sr;music=np.zeros(len(t),np.float32)
for start in np.arange(0,total,8):
 idx=int(start*sr);length=min(int(9*sr),len(t)-idx);tt=np.arange(length)/sr
 notes=([146.83,220,277.18,329.63],[130.81,196,246.94,329.63],[164.81,246.94,293.66,369.99],[110,164.81,220,277.18])[int(start/8)%4]
 env=np.minimum(tt/2,1)*np.minimum((length/sr-tt)/3,1)
 music[idx:idx+length]+=sum(np.sin(2*np.pi*f*tt+.15*np.sin(tt*.4)) for f in notes)/4*env*.045
music*=np.minimum(t/.5,1)*np.minimum((60-t)/1.5,1)
with wave.open(str(ROOT/'audio/score-60s.wav'),'w') as f:
 f.setnchannels(1);f.setsampwidth(2);f.setframerate(sr);f.writeframes((music*32767).astype(np.int16).tobytes())
run([FF,'-y','-i',str(ROOT/'source/picture-60s.mp4'),'-i',str(ROOT/'audio/score-60s.wav'),'-c:v','copy','-c:a','aac','-b:a','192k','-t','60','-metadata','title=Easy 60-second visual preview — narration pending','-movflags','+faststart',str(ROOT/'Easy-60s-visual-preview.mp4')])
print('Visual preview ready; Fish Audio narration is pending.',flush=True)
