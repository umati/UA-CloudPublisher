# simulator device 1 for mqtt message publishing
import random
import time

import paho.mqtt.client as paho

# brokersettings
BROKER = "localhost"  # mqtt broker url + port exposed to local
PORT = 1883


def on_publish(client, userdata, result):
    print("Device 1 : Data published.")
    pass


client = paho.Client(client_id="admin")
client.on_publish = on_publish
client.connect(host=BROKER, port=PORT)
for i in range(20):
    d = random.randint(1, 5)

    # telemetry to send
    MESSAGE = "Device 1 : Data " + str(i)
    time.sleep(d)

    # publish message
    ret = client.publish(topic="data", payload=MESSAGE)

print("Stopped...")
