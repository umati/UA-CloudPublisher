﻿
namespace UA.MQTT.Publisher.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using UA.MQTT.Publisher.Interfaces;
    using UA.MQTT.Publisher.Models;

    public class ConfigController : Controller
    {
        private readonly IMQTTSubscriber _subscriber;

        public ConfigController(IMQTTSubscriber subscriber)
        {
            _subscriber = subscriber;
        }

        public IActionResult Index()
        {
            return View("Index", Settings.Singleton);
        }

        [HttpPost]
        public IActionResult Update(Settings settings)
        {
            if (ModelState.IsValid)
            {
                Settings.Singleton = settings;
                Settings.Singleton.Save();

                // reconnect to broker with new settings
                _subscriber.Connect();
            }

            return View("Index", Settings.Singleton);
        }
    }
}
