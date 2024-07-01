import paho.mqtt.client as mqtt

# broker settings
BROKER = "localhost"  # mqtt broker url + port exposed to local
PORT = 1883

# time for Subscriber to live
TIMELIVE = 60


def on_connect(client, userdata, flags, rc):
    print("Connected with result code " + str(rc))
    client.subscribe(topic="data")


def on_message(client, userdata, msg):
    print(msg.payload.decode())


sub_client = mqtt.Client()
sub_client.connect(host=BROKER, port=PORT, keepalive=TIMELIVE)
sub_client.on_connect = on_connect
sub_client.on_message = on_message
sub_client.loop_forever()
