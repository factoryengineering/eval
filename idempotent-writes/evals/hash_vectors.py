import hashlib, base64
def h(s): return base64.urlsafe_b64encode(hashlib.sha256(s.encode()).digest()).decode().rstrip('=')
i="0f8fad5b-d9cb-469f-a165-70867728950e"; p="7c9e6679-7425-40de-944b-e07fc1f90ae7"
print(h('{"id":"%s","subject":"printer","priority":3,"escalated":false,"category":"hardware"}'%i))
print(h('{"id":"%s","warehouseId":"%s","reference":"SHP-1","weightKg":12.5,"fragile":true}'%(i,p)))
print(h('{"id":"%s","weightKg":100}'%i))
print(h('{"id":"%s","weightKg":0.05}'%i))
