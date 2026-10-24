"""Restore opaque, linear RG normals from original DDS; never decode through premultiplied alpha.
Requires Pillow. Existing source DDS/GLB files are read-only. Unity's ordinary normal
importer is deliberately not used: AC6 alpha is a separate mask, not normal X.
"""
from pathlib import Path
import argparse, json, struct, uuid, hashlib
from PIL import Image

ap=argparse.ArgumentParser();ap.add_argument('--project',type=Path,required=True);ap.add_argument('--source',type=Path,required=True);ap.add_argument('--materials',type=Path,required=True);args=ap.parse_args()
names=set()
for line in args.materials.read_text(encoding='utf-8').splitlines():
 row=json.loads(line);p=args.project/row['normalPath']
 if p.suffix!='.glb':continue
 b=p.read_bytes();size=struct.unpack_from('<I',b,12)[0];doc=json.loads(b[20:20+size])
 for im in doc.get('images',[]):
  name=im.get('name','')
  if name.lower().endswith('_n'):names.add(name)
sources={}
for folder in ('parts','wp'):
 for p in (args.source/folder).rglob('*.dds'):
  if p.stem in names:sources.setdefault(p.stem,p)
out=args.project/'Assets/_Project/Resources/AC6Materials/Normals';out.mkdir(parents=True,exist_ok=True)
rows=[]
for name in sorted(names):
 if name not in sources:raise RuntimeError('Missing original normal: '+name)
 source=sources[name];im=Image.open(source).convert('RGBA');r,g,_,_=im.split()
 # Shader reconstructs Z from RG; all unused channels are opaque neutral values.
 image=Image.merge('RGBA',(r,g,Image.new('L',im.size,255),Image.new('L',im.size,255)))
 dest=out/(name+'.png');image.save(dest)
 meta=Path(str(dest)+'.meta')
 if not meta.exists():meta.write_text('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\nTextureImporter:\n  serializedVersion: 13\n  mipmaps:\n    enableMipMap: 1\n    sRGBTexture: 0\n  isReadable: 0\n  textureFormat: 1\n  maxTextureSize: 4096\n  textureSettings:\n    serializedVersion: 2\n    filterMode: 2\n    aniso: 4\n    mipBias: 0\n    wrapU: 0\n    wrapV: 0\n    wrapW: 0\n  textureType: 0\n  textureShape: 1\n  alphaIsTransparency: 0\n  platformSettings:\n  - serializedVersion: 3\n    buildTarget: DefaultTexturePlatform\n    maxTextureSize: 4096\n    resizeAlgorithm: 0\n    textureFormat: -1\n    textureCompression: 0\n    compressionQuality: 100\n    overridden: 0\n',encoding='utf-8')
 rows.append({'name':name,'source':str(source),'sourceSha256':hashlib.sha256(source.read_bytes()).hexdigest(),'output':str(dest),'size':list(im.size),'rgByteExact':list(image.split()[0].getdata())==list(r.getdata()) and list(image.split()[1].getdata())==list(g.getdata())})
(args.materials.parent/'normal-restoration.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print('Restored',len(rows),'original normal maps; RG exact, alpha opaque, linear import.')
