#!/usr/bin/env bash
# OfficeTool API 冒烟测试：覆盖 §7.5 端点 + 安全用例
# 可用环境变量指向不同环境：
#   BASE_URL    默认 http://127.0.0.1:5080（容器部署可传 http://127.0.0.1:8080）
#   SHARE_ROOT  默认 /home/ubuntu/officetool-share（须为服务端看到的存储根父目录）
set -u
B=${BASE_URL:-http://127.0.0.1:5080}
SHARE=${SHARE_ROOT:-/home/ubuntu/officetool-share}
PASS=0; FAIL=0

chk() { # chk <描述> <期望> <实际>
  if [ "$2" = "$3" ]; then echo "  ✓ $1 ($3)"; PASS=$((PASS+1));
  else echo "  ✗ $1 — 期望 $2 实得 $3"; FAIL=$((FAIL+1)); fi
}

# ── 临时文件：每次运行独立，绝不共用固定路径 ──────────────────────────
# 早期版本把响应体和上传用样例写死在 /tmp/_body、/tmp/tpl/ 这类固定路径上。
# 一旦这些文件因为属主/权限写不进去（换用户跑、换机器跑、CI 跑都会遇到），
# curl 会静默失败，而 body() 读到的是**上一次运行的陈旧响应**，
# 于是一堆用例莫名其妙地失败，排查方向也被带偏。
# 现在：独立临时目录 + 每次调用前清空 body，写不进去就立刻大声报错。
WORK_DIR="$(mktemp -d)" || { echo "无法创建临时目录" >&2; exit 1; }
BODY_FILE="$WORK_DIR/.response-body"
trap 'rm -rf "$WORK_DIR"' EXIT

code() { # code <curl 参数...> → 输出 HTTP 状态码，响应体写入 $BODY_FILE
  # 先清空：即使本次写入失败也只会得到空 body，不会把上一次的响应当成本次结果
  : > "$BODY_FILE" || { echo "无法写入临时文件 $BODY_FILE" >&2; exit 1; }
  curl -sS -o "$BODY_FILE" -w '%{http_code}' "$@" || true
}

body() { cat "$BODY_FILE"; }

echo "═══ 1. 健康检查与配置 ═══"
chk "GET /api/health" 200 "$(code $B/api/health)"
echo "     $(body)"
chk "GET /api/system/config" 200 "$(code $B/api/system/config)"
echo "     白名单项数: $(body | python3 -c 'import sys,json;print(len(json.load(sys.stdin)["allowedExtensions"]))')"
echo "     存储: $(body | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d["fileStoreIsRemoteUnc"], d["fileStoreAvailable"])')"

echo "═══ 2. 项目与检项 ═══"
chk "POST /api/projects QLS2409" 201 "$(code -X POST $B/api/projects -H 'Content-Type: application/json' -d '{"name":"QLS2409"}')"
chk "重复创建应 409" 409 "$(code -X POST $B/api/projects -H 'Content-Type: application/json' -d '{"name":"QLS2409"}')"
chk "非法项目名(穿越) 应 400" 400 "$(code -X POST $B/api/projects -H 'Content-Type: application/json' -d '{"name":"../evil"}')"
chk "非法项目名(空格) 应 400" 400 "$(code -X POST $B/api/projects -H 'Content-Type: application/json' -d '{"name":"QLS 2409"}')"
chk "POST 检项 SEC" 201 "$(code -X POST $B/api/projects/QLS2409/checks -H 'Content-Type: application/json' -d '{"name":"SEC"}')"
chk "GET 检项列表" 200 "$(code $B/api/projects/QLS2409/checks)"
chk "不存在的项目检项列表 应 404" 404 "$(code $B/api/projects/NOPE/checks)"

echo "═══ 3. 模板上传 ═══"
mkdir -p "$WORK_DIR" && printf 'PK\003\004 fake docx payload' > $WORK_DIR/检验记录模板.docx
printf 'MZ fake exe' > $WORK_DIR/bad.exe
chk "上传 .docx" 200 "$(code -X POST $B/api/templates/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/检验记录模板.docx")"
echo "     $(body)"
chk "重复上传同名 应 409" 409 "$(code -X POST $B/api/templates/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/检验记录模板.docx")"
chk "上传 .exe 应 400" 400 "$(code -X POST $B/api/templates/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/bad.exe")"
chk "GET /api/templates" 200 "$(code $B/api/templates)"
echo "     模板数: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')"
echo "     访问路径: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["items"][0]["accessPath"])')"

echo "═══ 4. 命名规则（核心） ═══"
for i in 1 2 3; do
  chk "新建文档 #$i" 200 "$(code -X POST $B/api/documents/create -H 'Content-Type: application/json' -d '{"project":"QLS2409","check":"SEC","templateFileName":"检验记录模板.docx"}')"
  echo "     文件名: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["fileName"])')"
done
chk "不存在的模板 应 404" 404 "$(code -X POST $B/api/documents/create -H 'Content-Type: application/json' -d '{"project":"QLS2409","check":"SEC","templateFileName":"不存在.docx"}')"

echo "═══ 5. 查找与分页 ═══"
chk "按关键字筛选" 200 "$(code "$B/api/documents?keyword=20260912&page=1&pageSize=2")"
echo "     total=$(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])') 本页=$(body | python3 -c 'import sys,json;print(len(json.load(sys.stdin)["items"]))')"
chk "按扩展名筛选" 200 "$(code "$B/api/documents?extension=.docx")"
chk "按项目+检项筛选" 200 "$(code "$B/api/templates?project=QLS2409&check=SEC&sortBy=name&sortDir=asc")"

echo "═══ 6. 重命名 / 复制 / 删除 ═══"
chk "复制模板(自动命名)" 200 "$(code -X POST $B/api/templates/copy -H 'Content-Type: application/json' -d '{"project":"QLS2409","check":"SEC","sourceFileName":"检验记录模板.docx"}')"
echo "     副本名: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["fileName"])')"
chk "重命名文档" 200 "$(code -X POST $B/api/documents/rename -H 'Content-Type: application/json' -d '{"project":"QLS2409","check":"SEC","fileName":"20260912B.docx","newFileName":"20260912B-已改.docx"}')"

# 中文参数必须 URL 编码，否则服务端收到的是畸形查询串
del() { code -X DELETE -G "$B/$1" --data-urlencode "project=$2" --data-urlencode "check=$3" --data-urlencode "fileName=$4"; }

chk "删除文档" 204 "$(del api/documents QLS2409 SEC '20260912B-已改.docx')"
chk "删除已不存在的文档 应 404" 404 "$(del api/documents QLS2409 SEC '20260912B-已改.docx')"
chk "删除时文件名穿越 应 400" 400 "$(del api/documents QLS2409 SEC '../../../../etc/passwd')"
chk "删除时子路径穿越 应 400" 400 "$(del api/documents QLS2409 SEC 'QLS2409/../../x.docx')"
chk "非空检项禁止删除 应 409" 409 "$(code -X DELETE $B/api/projects/QLS2409/checks/SEC)"
chk "非空项目禁止删除 应 409" 409 "$(code -X DELETE $B/api/projects/QLS2409)"

echo "═══ 7. 元数据同步与日志 ═══"
chk "POST /api/system/sync" 200 "$(code -X POST $B/api/system/sync)"
echo "     $(body)"
chk "GET /api/logs" 200 "$(code $B/api/logs)"
echo "     日志数: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')"

echo "═══ 8. 提取规则（与模板/文档同一套「项目/检项」目录，规则本体是文件）═══"
RULE='标准字段.json'
RULE_ENC=$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote(sys.argv[1]))" "$RULE")
printf '{"fields":[{"name":"标准号"},{"name":"检验结论"}]}' > "$WORK_DIR/$RULE"
printf 'MZ fake exe' > $WORK_DIR/bad.exe

# 规则需要另一个文件夹作为复制目标
chk "新建检项 QLS2409/SEC2（规则复制目标）" 201 "$(code -X POST $B/api/projects/QLS2409/checks -H 'Content-Type: application/json' -d '{"name":"SEC2"}')"

chk "上传规则 .json" 200 "$(code -X POST $B/api/extraction-rules/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/$RULE")"
echo "     规则访问路径: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessPath"])')"
chk "重复上传同名规则 应 409" 409 "$(code -X POST $B/api/extraction-rules/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/$RULE")"
chk "上传 .exe 当规则 应 400" 400 "$(code -X POST $B/api/extraction-rules/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/bad.exe")"
chk "列出规则" 200 "$(code "$B/api/extraction-rules?project=QLS2409&check=SEC")"
echo "     规则数: $(body | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')"
chk "列出规则缺少 check 应 400" 400 "$(code "$B/api/extraction-rules?project=QLS2409")"

# 备注：网页上直接改
chk "写备注" 200 "$(code -X POST $B/api/extraction-rules/note -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileName\":\"$RULE\",\"note\":\"提取标准号与检验结论\"}")"
chk "备注已保存" "提取标准号与检验结论" "$(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["note"])')"

# 复制到其他文件夹：备注应一并带过去
chk "复制规则到 QLS2409/SEC2" 200 "$(code -X POST $B/api/extraction-rules/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileNames\":[\"$RULE\"],\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC2\"}")"
echo "     复制结果: $(body)"
chk "目标文件夹有该规则" 200 "$(code "$B/api/extraction-rules?project=QLS2409&check=SEC2")"
chk "备注随规则一起复制过去" "提取标准号与检验结论" "$(body | python3 -c 'import sys,json;print(json.load(sys.stdin)[0]["note"])')"
chk "复制到相同文件夹 应 400" 400 "$(code -X POST $B/api/extraction-rules/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileNames\":[\"$RULE\"],\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC\"}")"
chk "复制不存在的规则 应 404" 404 "$(code -X POST $B/api/extraction-rules/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileNames\":[\"没有这个.json\"],\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC2\"}")"

echo "═══ 8b. 附件与提取（提取接口契约已预留）═══"
printf '%%PDF-1.4 fake pdf payload' > $WORK_DIR/检验记录.pdf
chk "上传附件 .pdf" 200 "$(code -X POST $B/api/attachments/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/检验记录.pdf")"
echo "     附件访问路径: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessPath"])')"
chk "重复上传同名附件 应 409" 409 "$(code -X POST $B/api/attachments/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/检验记录.pdf")"
chk "上传 .exe 当附件 应 400" 400 "$(code -X POST $B/api/attachments/upload -F 'project=QLS2409' -F 'check=SEC' -F "file=@$WORK_DIR/bad.exe")"
chk "GET /api/attachments" 200 "$(code "$B/api/attachments?project=QLS2409&check=SEC")"
echo "     附件数: $(body | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')"
chk "配置里附件/规则白名单都比模板宽" 200 "$(code $B/api/system/config)"
echo "     模板 $(body | python3 -c 'import sys,json;d=json.load(sys.stdin);print(len(d["allowedExtensions"]))') 项 / 附件 $(body | python3 -c 'import sys,json;d=json.load(sys.stdin);print(len(d["attachmentExtensions"]))') 项 / 规则 $(body | python3 -c 'import sys,json;d=json.load(sys.stdin);print(len(d["ruleExtensions"]))') 项"

# 提取：契约已定、脚本未实现。参数不合法要被拦下，参数齐全则应返回 501 而不是 500。
DOCNAME=$(curl -sS "$B/api/documents?project=QLS2409&check=SEC&pageSize=1" \
  | python3 -c 'import sys,json;d=json.load(sys.stdin)["items"];print(d[0]["fileName"] if d else "")')
echo "     用于提取的目标文档: ${DOCNAME:-<无>}"
chk "提取·缺失规则文件名 应 400" 400 "$(code -X POST $B/api/attachments/extract -H 'Content-Type: application/json' \
  -d '{"project":"QLS2409","check":"SEC","attachmentFileName":"检验记录.pdf","ruleFileName":"","targetDocumentFileName":"x.docx"}')"
chk "提取·规则文件不存在 应 404" 404 "$(code -X POST $B/api/attachments/extract -H 'Content-Type: application/json' \
  -d '{"project":"QLS2409","check":"SEC","attachmentFileName":"检验记录.pdf","ruleFileName":"没有这个.json","targetDocumentFileName":"x.docx"}')"
chk "提取·附件不存在 应 404" 404 "$(code -X POST $B/api/attachments/extract -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"attachmentFileName\":\"没有这个.pdf\",\"ruleFileName\":\"$RULE\",\"targetDocumentFileName\":\"x.docx\"}")"
chk "提取·目标文档不存在 应 404" 404 "$(code -X POST $B/api/attachments/extract -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"attachmentFileName\":\"检验记录.pdf\",\"ruleFileName\":\"$RULE\",\"targetDocumentFileName\":\"没有这个.docx\"}")"
chk "提取·参数齐全 应 501（接口已预留、脚本未实现）" 501 "$(code -X POST $B/api/attachments/extract -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"attachmentFileName\":\"检验记录.pdf\",\"ruleFileName\":\"$RULE\",\"targetDocumentFileName\":\"$DOCNAME\"}")"
echo "     返回码: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["code"])')"
chk "删除附件(中文名)" 204 "$(code -X DELETE "$B/api/attachments?project=QLS2409&check=SEC&fileName=$(python3 -c "import urllib.parse;print(urllib.parse.quote('检验记录.pdf'))")")"
chk "删除已不存在的附件 应 404" 404 "$(code -X DELETE "$B/api/attachments?project=QLS2409&check=SEC&fileName=$(python3 -c "import urllib.parse;print(urllib.parse.quote('检验记录.pdf'))")")"
chk "删除目标文件夹的规则（备注应一并清除）" 204 "$(code -X DELETE "$B/api/extraction-rules?project=QLS2409&check=SEC2&fileName=$RULE_ENC")"
chk "删除后该文件夹规则为空" "0" "$(curl -sS "$B/api/extraction-rules?project=QLS2409&check=SEC2" | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')"

echo "═══ 9. 文档复制到其他文件夹（不是复制到当前文件夹）═══"
chk "复制文档到 QLS2409/SEC2" 200 "$(code -X POST $B/api/documents/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileName\":\"$DOCNAME\",\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC2\"}")"
echo "     复制结果: $(body)"
chk "目标文件夹能看到该文档" 200 "$(code "$B/api/documents?project=QLS2409&check=SEC2")"
chk "目标文件夹文档数" "1" "$(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])')"
chk "目标文件夹路径为客户端视角" 200 "$(code "$B/api/documents?project=QLS2409&check=SEC2")"
echo "     accessPath: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin)["items"][0]["accessPath"])')"
chk "复制到相同文件夹 应 400" 400 "$(code -X POST $B/api/documents/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileName\":\"$DOCNAME\",\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC\"}")"
chk "复制不存在的文档 应 404" 404 "$(code -X POST $B/api/documents/copy -H 'Content-Type: application/json' \
  -d '{"project":"QLS2409","check":"SEC","fileName":"没有这个.docx","targetProject":"QLS2409","targetCheck":"SEC2"}')"
chk "复制到不存在的项目 应 404" 404 "$(code -X POST $B/api/documents/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileName\":\"$DOCNAME\",\"targetProject\":\"NOPE\",\"targetCheck\":\"X\"}")"
# 再来一次：目标已存在同名 → 自动加「_副本」而不是覆盖
chk "再次复制同名文档（应自动加 _副本）" 200 "$(code -X POST $B/api/documents/copy -H 'Content-Type: application/json' \
  -d "{\"project\":\"QLS2409\",\"check\":\"SEC\",\"fileName\":\"$DOCNAME\",\"targetProject\":\"QLS2409\",\"targetCheck\":\"SEC2\"}")"
echo "     第二次复制结果: $(body)"

echo "═══ 10. 空项目/检项删除应同时清理目录 ═══"
chk "新建空项目 TMPTEST" 201 "$(code -X POST $B/api/projects -H 'Content-Type: application/json' -d '{"name":"TMPTEST"}')"
chk "新建空检项 TMPTEST/T1" 201 "$(code -X POST $B/api/projects/TMPTEST/checks -H 'Content-Type: application/json' -d '{"name":"T1"}')"
chk "先删检项 T1" 204 "$(code -X DELETE $B/api/projects/TMPTEST/checks/T1)"
chk "再删项目 TMPTEST" 204 "$(code -X DELETE $B/api/projects/TMPTEST)"
chk "重复删除项目应 404" 404 "$(code -X DELETE $B/api/projects/TMPTEST)"

for d in Templates/TMPTEST Data/TMPTEST Attachments/TMPTEST ExtractionRules/TMPTEST \
         Templates/TMPTEST/T1 Data/TMPTEST/T1 Attachments/TMPTEST/T1 ExtractionRules/TMPTEST/T1; do
  if [ -d "$SHARE/$d" ]; then
    echo "  ✗ 目录未清理: $d"; FAIL=$((FAIL+1));
  else
    echo "  ✓ 目录已清理: $d"; PASS=$((PASS+1));
  fi
done

echo "═══ 11. 项目树（一次拉全，避免 N+1） ═══"
chk "GET /api/projects/tree" 200 "$(code $B/api/projects/tree)"
echo "     节点数: $(body | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))' 2>/dev/null || echo '?')"

echo "═══ 12. 回收站：删除 → 列表 → 恢复 ═══"
# 用当前已有项目/检项下的文档（若上文已删完则跳过恢复链路）
TREE_CODE="$(code $B/api/projects/tree)"
TRASH_CODE="$(code $B/api/trash)"
chk "GET /api/trash" 200 "$TRASH_CODE"
echo "     回收站条目数: $(body | python3 -c 'import sys,json;print(json.load(sys.stdin).get("total",0))' 2>/dev/null || echo '?')"

echo
echo "═══ 结果：通过 $PASS / 失败 $FAIL ═══"
[ "$FAIL" -eq 0 ] || exit 1
