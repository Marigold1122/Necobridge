import fs from 'node:fs';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
const dir='D:/code/nekobridge/.tools/downloads/sdk-chunks';
fs.mkdirSync(dir,{recursive:true});
const size=303618098,count=24,chunk=Math.ceil(size/count);
await Promise.all(Array.from({length:count},(_,i)=>new Promise((resolve,reject)=>{
  const start=i*chunk,end=Math.min(size-1,start+chunk-1),file=`${dir}/${i}.part`;
  const child=spawn('curl',['-fsSL','--retry','2','--max-time','900','-r',`${start}-${end}`,'https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip','-o',file],{stdio:['ignore','ignore','pipe']});
  child.on('error',reject);child.on('exit',code=>{if(code!==0)return reject(new Error(`SDK chunk ${i} failed: ${code}`)); if(fs.statSync(file).size!==end-start+1)return reject(new Error(`SDK chunk ${i} wrong length`));console.log(`SDK chunk ${i+1}/${count} complete`);resolve();});
})));
const output=`${dir}/sdk.zip`,fd=fs.openSync(output,'w'),hash=crypto.createHash('sha512');
try{for(let i=0;i<count;i++){let bytes=fs.readFileSync(`${dir}/${i}.part`);fs.writeSync(fd,bytes);hash.update(bytes);}}finally{fs.closeSync(fd);}
if(hash.digest('hex')!=='330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee')throw new Error('SDK SHA512 mismatch');
console.log('Official SDK full SHA512 verified');
