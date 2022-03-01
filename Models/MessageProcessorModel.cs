﻿
namespace UA.MQTT.Publisher.Models
{
    using Opc.Ua;
    using System.Collections.Generic;

    public class MessageProcessorModel
    {
        public string ExpandedNodeId { get; set; }

        public string DataSetWriterId { get; set; }

        public DataValue Value { get; set; }

        public IServiceMessageContext MessageContext { get; set; }

        public List<EventValueModel> EventValues { get; set; } = new List<EventValueModel>();
    }
}
