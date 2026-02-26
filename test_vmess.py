import base64
b = "TNhIiwNCiAgImFpZCI6ICIwIiwNCiAgInNjeSI6ICJub25lIiwNCiAgIm5ldCI6ICJ3cyIsDQogICJ0eXBlIjogIm5vbmUiLA0KICAiaG9zdCI6ICJsb2NroYXJlaG9sZGVycy1kaWQtZnJudHJ5Y2xvdWRmbGFyZS5jb20iLA0KICAicGF0aCI6ICIvdm1lc3MtYXI"
b = b.replace('-','+').replace('_','/')
pad = len(b) % 4
if pad: b += '=' * (4 - pad)
try:
    decoded = base64.b64decode(b).decode('utf-8','replace')
    print("DECODED:", decoded)
except Exception as e:
    print("ERROR:", e)
