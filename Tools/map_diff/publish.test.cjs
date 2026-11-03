const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const {loadReport,previewBytes,body,publish,storeImages,MARKER}=require('./publish.cjs');
const head='a'.repeat(40),base='b'.repeat(40);
const scratch=path.resolve(__dirname,'../../.local/codex/map-diff-publish-tests');
fs.mkdirSync(scratch,{recursive:true});
function fixture(report){const dir=fs.mkdtempSync(path.join(scratch,'case-'));if(report)fs.writeFileSync(path.join(dir,'report.json'),JSON.stringify(report));return dir;}
const report=()=>({version:1,pr:3,head_sha:head,base_sha:base,maps:[]});
function client({stale=false,previous=true}={}){
 const writes=[],pr={number:3,state:'open',head:{sha:stale?'c'.repeat(40):head},base:{sha:base,repo:{id:5}}};
 const comments=previous?[{id:12,user:{login:'github-actions[bot]'},body:MARKER+' old'}]:[];
 const github={rest:{pulls:{get:async()=>({data:pr})},issues:{listComments:()=>{},updateComment:async x=>writes.push(x),createComment:async x=>writes.push(x)},repos:{listPullRequestsAssociatedWithCommit:()=>{}}},paginate:async fn=>fn===github.rest.issues.listComments?comments:[{number:3,head:{sha:head}}]};
 return {github,writes,context:{repo:{owner:'DeepLagoon',repo:'Station'},payload:{repository:{id:5},workflow_run:{id:7,event:'pull_request',head_sha:head,pull_requests:[{number:3,head:{sha:head}}]}}},core:{info:()=>{}}};
}
test('rejects nonnumeric PRs, injected filenames and non-PNG data',()=>{
 assert.throws(()=>loadReport(fixture({...report(),pr:'3;bad'})));
 assert.throws(()=>previewBytes(scratch,'../../script.js'));
 const dir=fixture(report());fs.writeFileSync(path.join(dir,'preview-000.png'),'<svg/>');assert.throws(()=>previewBytes(dir,'preview-000.png'));
});
test('escapes map paths and never emits artifact-supplied URLs',()=>{
 const r=report();r.maps=[{path:'[x](javascript:bad)\n@everyone',grids:[]}];
 const text=body(r,{},'https://github.com/example/run');assert.ok(!text.includes('[x]'));assert.ok(!text.includes('@everyone'));
});
test('stale workflow cannot overwrite current comment',async()=>{
 const c=client({stale:true});await publish({...c,artifactDir:fixture(report())});assert.equal(c.writes.length,0);
});
test('updates one existing comment when map changes are removed',async()=>{
 const c=client();await publish({...c,artifactDir:fixture(report())});assert.equal(c.writes.length,1);assert.equal(c.writes[0].comment_id,12);assert.match(c.writes[0].body,/нет изменений/);
});
test('does not create comments for unrelated PRs',async()=>{
 const c=client({previous:false});await publish({...c,artifactDir:fixture(report())});assert.equal(c.writes.length,0);
});
test('fork runs with no pull_requests use GitHub association, not artifact identity',async()=>{
 const c=client();c.context.payload.workflow_run.pull_requests=[];await publish({...c,artifactDir:fixture(report())});assert.equal(c.writes.length,1);
 const bad=client();bad.context.payload.workflow_run.pull_requests=[];await publish({...bad,artifactDir:fixture({...report(),pr:999})});assert.equal(bad.writes.length,0);
});
test('publishes a failure comment when renderer could not upload a report',async()=>{
 const c=client();await publish({...c,artifactDir:fixture()});assert.match(c.writes[0].body,/Не удалось/);
});
test('successful skipped render jobs do not create failure comments on title edits',async()=>{
 const c=client();c.context.payload.workflow_run.conclusion='success';await publish({...c,artifactDir:fixture()});assert.equal(c.writes.length,0);
});
test('publishes PNGs to an orphan preview branch and embeds immutable image URLs',async()=>{
 const c=client(),dir=fixture();
 const r=report();r.maps=[{path:'Resources/Maps/example.yml',grids:[{name:'Example',preview:'preview-000.png',stats:{objects_added:1}}]}];
 fs.writeFileSync(path.join(dir,'report.json'),JSON.stringify(r));fs.writeFileSync(path.join(dir,'preview-000.png'),Buffer.from([137,80,78,71,13,10,26,10]));
 const refs=[];c.github.rest.git={
  getRef:async()=>{throw {status:404};},createBlob:async()=>({data:{sha:head}}),createTree:async()=>({data:{sha:head}}),
  createCommit:async args=>{assert.deepEqual(args.parents,[]);return {data:{sha:head}};},createRef:async args=>refs.push(args)
 };
 await publish({...c,artifactDir:dir});assert.equal(refs[0].ref,'refs/heads/map-diff-previews');assert.match(c.writes[0].body,new RegExp(`raw.githubusercontent.com/DeepLagoon/Station/${head}/pr-3/preview-000.png`));
});
test('concurrent image publications retry without force-updating a branch',async()=>{
 let writes=0;const github={rest:{git:{
  createBlob:async()=>({data:{sha:head}}),createTree:async()=>({data:{sha:head}}),
  getRef:async()=>({data:{object:{sha:base}}}),getCommit:async()=>({data:{tree:{sha:base}}}),
  createCommit:async()=>({data:{sha:head}}),updateRef:async args=>{assert.equal(args.force,false);if(++writes===1)throw {status:422};}
 }}};
 assert.equal(await storeImages(github,{owner:'O',repo:'R'},3,[['preview-000.png',Buffer.from('png')]]),head);assert.equal(writes,2);
});
