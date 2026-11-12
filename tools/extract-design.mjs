import fs from 'node:fs';
import path from 'node:path';
const root='D:/code/nekobridge';
const input=path.join(root,'design/user-directed');
const output=path.join(root,'.tools/design-extracted');
const manifest=JSON.parse(fs.readFileSync(path.join(input,'FINAL-READY.json'),'utf8'));
if(!manifest.complete)throw new Error('Final design is not complete');
const groups=[];
for(const name of manifest.sourceOrder){
  if(!/^[a-z0-9-]+\.md$/i.test(name))throw new Error('Unsafe source filename');
  let text=fs.readFileSync(path.join(input,name),'utf8');
  if(/continuation|cont-\d/.test(name)){
    const match=/```(?:xml|csharp)\r?\n/.exec(text);
    if(!match||!groups.length)throw new Error('Missing continuation fence');
    groups[groups.length-1].text+=text.slice(match.index+match[0].length);
  }else groups.push({name,text});
}
fs.mkdirSync(output,{recursive:true});
const entries=new Map();
for(const group of groups){
 fs.writeFileSync(path.join(output,group.name),group.text);
 let file=null,part=false;
 const lines=group.text.split('\n');
 for(let i=0;i<lines.length;i++){
  const heading=/^(?:##\s+|\*\*)([A-Za-z][A-Za-z0-9_./-]*\.(?:xaml(?:\.cs)?|csproj|cs|manifest))(.*)$/.exec(lines[i]);
  if(heading){file=heading[1];part=heading[2].includes('改动');}
  if(/^```(?:xml|csharp)\s*$/.test(lines[i])){
   const start=++i;while(i<lines.length&&!/^```\s*$/.test(lines[i]))i++;
   if(i===lines.length)throw new Error('Unclosed code fence in '+group.name);
   if(!file||part)continue;
   if(file.includes('..')||path.isAbsolute(file)||!/^\w[\w./-]+$/.test(file))throw new Error('Unsafe design path '+file);
   const code=lines.slice(start,i).join('\n')+'\n';
   entries.set(file,{code,source:group.name});
  }
 }
}
for(const [file,{code}]of entries){const target=path.join(output,'final',file);fs.mkdirSync(path.dirname(target),{recursive:true});fs.writeFileSync(target,code);}
fs.writeFileSync(path.join(output,'files.json'),JSON.stringify([...entries].map(([file,{source}])=>({file,source})),null,2));
console.log(JSON.stringify([...entries].map(([file,{source}])=>({file,source})),null,2));
