using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using MySql.Data.MySqlClient;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        // Cliente MQTT
        private MqttClient mqttClient;
        private bool isConnected = false;

        // Temporizadores para automatización
        private Dictionary<string, DispatcherTimer> timersApagado = new Dictionary<string, DispatcherTimer>();
        private Dictionary<string, bool> estadoMovimiento = new Dictionary<string, bool>();

        // Base de datos MySQL
        private string dbConnectionString = "Server=127.0.0.1;Port=3306;Database=domotica;Uid=root;Pwd=27182;";

        // Colección de eventos para el ListBox
        private ObservableCollection<EventoLog> eventos = new ObservableCollection<EventoLog>();

        // Jerarquía de tópicos MQTT
        private const string BASE_TOPIC = "casa";

        public MainWindow()
        {
            InitializeComponent();
            InitializeSystem();
        }

        private void InitializeSystem()
        {
            // Configurar ListBox de eventos
            listEventos.ItemsSource = eventos;

            // Inicializar base de datos
            InitializeDatabase();

            // Configurar temporizadores para cada zona con sensor de movimiento
            string[] zonasConSensor = { "sala", "porche" };
            foreach (var zona in zonasConSensor)
            {
                var timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromMinutes(5); // 5 minutos de inactividad
                timer.Tag = zona;
                timer.Tick += Timer_Apagado_Tick;
                timersApagado[zona] = timer;
                estadoMovimiento[zona] = false;
            }

            // Temporizador para actualizar hora
            DispatcherTimer clockTimer = new DispatcherTimer();
            clockTimer.Interval = TimeSpan.FromSeconds(1);
            clockTimer.Tick += (s, e) => txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            clockTimer.Start();

            AgregarEvento("Sistema", "Sistema inicializado correctamente", "Sistema");
        }

        #region Base de Datos MySQL

        private void InitializeDatabase()
        {
            try
            {
                using (var connection = new MySqlConnection(dbConnectionString))
                {
                    connection.Open();

                    // Tabla de Eventos
                    string createEventosTable = @"
                        CREATE TABLE IF NOT EXISTS Eventos (
                            Id INT AUTO_INCREMENT PRIMARY KEY,
                            Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                            Equipo VARCHAR(100) NOT NULL,
                            Accion VARCHAR(50) NOT NULL,
                            Origen VARCHAR(50) NOT NULL,
                            Zona VARCHAR(50) NOT NULL,
                            INDEX idx_zona (Zona),
                            INDEX idx_timestamp (Timestamp)
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

                    // Tabla de Mediciones
                    string createMedicionesTable = @"
                        CREATE TABLE IF NOT EXISTS Mediciones (
                            Id INT AUTO_INCREMENT PRIMARY KEY,
                            Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                            Zona VARCHAR(50) NOT NULL,
                            TipoSensor VARCHAR(50) NOT NULL,
                            Valor DECIMAL(10,2) NOT NULL,
                            INDEX idx_zona (Zona),
                            INDEX idx_timestamp (Timestamp)
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

                    using (var cmd = new MySqlCommand(createEventosTable, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new MySqlCommand(createMedicionesTable, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                AgregarEvento("Database", "Base de datos MySQL inicializada", "Sistema");
            }
            catch (MySqlException ex)
            {
                MessageBox.Show($"Error al inicializar base de datos MySQL: {ex.Message}\n\nAsegúrate de que MySQL esté ejecutándose y las credenciales sean correctas.",
                    "Error de Conexión MySQL", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error general: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GuardarEvento(string equipo, string accion, string origen, string zona)
        {
            try
            {
                using (var connection = new MySqlConnection(dbConnectionString))
                {
                    connection.Open();
                    string query = "INSERT INTO Eventos (Equipo, Accion, Origen, Zona) VALUES (@equipo, @accion, @origen, @zona)";

                    using (var cmd = new MySqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@equipo", equipo);
                        cmd.Parameters.AddWithValue("@accion", accion);
                        cmd.Parameters.AddWithValue("@origen", origen);
                        cmd.Parameters.AddWithValue("@zona", zona);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (MySqlException ex)
            {
                Console.WriteLine($"Error MySQL al guardar evento: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al guardar evento: {ex.Message}");
            }
        }

        private void GuardarMedicion(string zona, string tipoSensor, double valor)
        {
            try
            {
                using (var connection = new MySqlConnection(dbConnectionString))
                {
                    connection.Open();
                    string query = "INSERT INTO Mediciones (Zona, TipoSensor, Valor) VALUES (@zona, @tipo, @valor)";

                    using (var cmd = new MySqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@zona", zona);
                        cmd.Parameters.AddWithValue("@tipo", tipoSensor);
                        cmd.Parameters.AddWithValue("@valor", valor);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (MySqlException ex)
            {
                Console.WriteLine($"Error MySQL al guardar medición: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al guardar medición: {ex.Message}");
            }
        }

        private List<EventoLog> ConsultarEventosRecientes(int limite = 50)
        {
            List<EventoLog> eventosDB = new List<EventoLog>();

            try
            {
                using (var connection = new MySqlConnection(dbConnectionString))
                {
                    connection.Open();
                    string query = @"SELECT Timestamp, Equipo, Accion, Origen 
                                   FROM Eventos 
                                   ORDER BY Timestamp DESC 
                                   LIMIT @limite";

                    using (var cmd = new MySqlCommand(query, connection))
                    {
                        cmd.Parameters.AddWithValue("@limite", limite);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                eventosDB.Add(new EventoLog
                                {
                                    Timestamp = reader.GetDateTime("Timestamp").ToString("HH:mm:ss"),
                                    Equipo = reader.GetString("Equipo"),
                                    Mensaje = $"[{reader.GetString("Equipo")}] {reader.GetString("Accion")} ({reader.GetString("Origen")})",
                                    Origen = reader.GetString("Origen")
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al consultar eventos: {ex.Message}");
            }

            return eventosDB;
        }

        private Dictionary<string, int> ObtenerEstadisticasPorZona()
        {
            Dictionary<string, int> stats = new Dictionary<string, int>();

            try
            {
                using (var connection = new MySqlConnection(dbConnectionString))
                {
                    connection.Open();
                    string query = @"SELECT Zona, COUNT(*) as Total 
                                   FROM Eventos 
                                   WHERE DATE(Timestamp) = CURDATE() 
                                   GROUP BY Zona";

                    using (var cmd = new MySqlCommand(query, connection))
                    {
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                stats[reader.GetString("Zona")] = reader.GetInt32("Total");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al obtener estadísticas: {ex.Message}");
            }

            return stats;
        }

        #endregion

        #region Conexión MQTT con Autenticación

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                ConnectToMQTT();
            }
            else
            {
                DisconnectFromMQTT();
            }
        }

        private void ConnectToMQTT()
        {
            try
            {
                string brokerIP = txtBrokerIP.Text;
                int port = int.Parse(txtPort.Text);
                string clientId = txtClientID.Text;

                // Validaciones básicas
                if (string.IsNullOrWhiteSpace(brokerIP))
                {
                    MessageBox.Show("Debe ingresar la IP del broker", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(clientId))
                {
                    MessageBox.Show("Debe ingresar un ID de cliente", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                mqttClient = new MqttClient(brokerIP, port, false, null, null, MqttSslProtocols.None);

                mqttClient.MqttMsgPublishReceived += MqttClient_MqttMsgPublishReceived;

                // NUEVA LÓGICA: Verificar si se debe usar autenticación
                bool useAuth = chkUseAuth?.IsChecked ?? false;

                if (useAuth)
                {
                    string username = txtUsername?.Text ?? "";
                    string password = txtPassword?.Password ?? "";

                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    {
                        MessageBox.Show("Debe ingresar usuario y contraseña cuando la autenticación está habilitada",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Conectar CON autenticación
                    mqttClient.Connect(clientId, username, password);
                    AgregarEvento("MQTT", $"Conectado con autenticación (Usuario: {username})", "Sistema");
                }
                else
                {
                    // Conectar SIN autenticación (anónimo)
                    mqttClient.Connect(clientId);
                    AgregarEvento("MQTT", "Conectado sin autenticación (modo anónimo)", "Sistema");
                }

                if (mqttClient.IsConnected)
                {
                    isConnected = true;
                    txtConnectionStatus.Text = "Conectado";
                    txtConnectionStatus.Foreground = new SolidColorBrush(Colors.Green);
                    btnConnect.Content = "Desconectar";
                    btnConnect.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));

                    // Suscribirse a todos los tópicos relevantes
                    SubscribeToTopics();

                    AgregarEvento("MQTT", $"Conectado a broker {brokerIP}:{port}", "Sistema");
                    txtStatusBar.Text = $"Conectado al broker MQTT - {brokerIP}:{port}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al conectar: {ex.Message}\n\nVerifique:\n• Que el broker esté ejecutándose\n• Las credenciales sean correctas\n• La IP y puerto sean válidos",
                    "Error de Conexión MQTT", MessageBoxButton.OK, MessageBoxImage.Error);
                AgregarEvento("MQTT", $"Error de conexión: {ex.Message}", "Sistema");
            }
        }

        private void DisconnectFromMQTT()
        {
            if (mqttClient != null && mqttClient.IsConnected)
            {
                mqttClient.Disconnect();
                isConnected = false;
                txtConnectionStatus.Text = "Desconectado";
                txtConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                btnConnect.Content = "Conectar";
                btnConnect.Background = new SolidColorBrush(Color.FromRgb(39, 174, 96));
                AgregarEvento("MQTT", "Desconectado del broker", "Sistema");
                txtStatusBar.Text = "Desconectado del broker MQTT";
            }
        }

        private void SubscribeToTopics()
        {
            try
            {
                // Suscribirse a estados de luces
                string[] zonas = { "porche", "sala", "cocina", "habitacion1", "habitacion2", "bano", "patio" };

                foreach (var zona in zonas)
                {
                    mqttClient.Subscribe(new string[] { $"{BASE_TOPIC}/{zona}/luz/estado" },
                                        new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                    mqttClient.Subscribe(new string[] { $"{BASE_TOPIC}/{zona}/temperatura" },
                                        new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                }

                // Suscribirse a aires acondicionados
                string[] zonasConAA = { "porche", "sala", "habitacion1", "habitacion2" };
                foreach (var zona in zonasConAA)
                {
                    mqttClient.Subscribe(new string[] { $"{BASE_TOPIC}/{zona}/aa/estado" },
                                        new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                }

                // Suscribirse a sensores de movimiento
                string[] zonasConSensor = { "sala", "porche" };
                foreach (var zona in zonasConSensor)
                {
                    mqttClient.Subscribe(new string[] { $"{BASE_TOPIC}/{zona}/sensor/movimiento" },
                                        new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });
                }

                AgregarEvento("MQTT", $"Suscrito a todos los tópicos bajo '{BASE_TOPIC}/#'", "Sistema");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al suscribirse a tópicos: {ex.Message}", "Error MQTT", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MqttClient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            string message = System.Text.Encoding.UTF8.GetString(e.Message);

            Dispatcher.Invoke(() =>
            {
                ProcessMQTTMessage(topic, message);
            });
        }

        private void ProcessMQTTMessage(string topic, string message)
        {
            string[] topicParts = topic.Split('/');

            if (topicParts.Length < 3) return;

            string zona = topicParts[1];
            string dispositivo = topicParts[2];

            // Procesar temperatura
            if (dispositivo == "temperatura")
            {
                if (double.TryParse(message, out double temp))
                {
                    ActualizarTemperatura(zona, temp);
                    GuardarMedicion(zona, "temperatura", temp);
                }
            }
            // Procesar estado de luz
            else if (dispositivo == "luz" && topicParts.Length > 3 && topicParts[3] == "estado")
            {
                bool estado = message.ToUpper() == "ON";
                ActualizarEstadoLuz(zona, estado);
            }
            // Procesar estado de AA
            else if (dispositivo == "aa" && topicParts.Length > 3 && topicParts[3] == "estado")
            {
                bool estado = message.ToUpper() == "ON";
                ActualizarEstadoAA(zona, estado);
            }
            // Procesar sensor de movimiento
            else if (dispositivo == "sensor" && topicParts.Length > 3 && topicParts[3] == "movimiento")
            {
                bool presencia = message.ToUpper() == "ON" || message == "1";
                ProcesarMovimiento(zona, presencia);
            }
        }

        #endregion

        #region Control de Dispositivos

        private void ToggleLuz_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                MessageBox.Show("Debe conectarse al broker MQTT primero", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleButton btn = sender as ToggleButton;
            string tag = btn.Tag.ToString();
            string[] parts = tag.Split('/');
            string zona = parts[0];
            bool estado = btn.IsChecked ?? false;

            PublishCommand($"{BASE_TOPIC}/{zona}/luz/comando", estado ? "ON" : "OFF");

            string accion = estado ? "Encendido" : "Apagado";
            AgregarEvento($"Luz {zona}", accion, "Usuario Manual");
            GuardarEvento($"Luz_{zona}", accion, "Usuario", zona);

            ActualizarEstadisticas();
        }

        private void ToggleAA_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected)
            {
                MessageBox.Show("Debe conectarse al broker MQTT primero", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ToggleButton btn = sender as ToggleButton;
            string tag = btn.Tag.ToString();
            string[] parts = tag.Split('/');
            string zona = parts[0];
            bool estado = btn.IsChecked ?? false;

            PublishCommand($"{BASE_TOPIC}/{zona}/aa/comando", estado ? "ON" : "OFF");

            string accion = estado ? "Encendido" : "Apagado";
            AgregarEvento($"AA {zona}", accion, "Usuario Manual");
            GuardarEvento($"AA_{zona}", accion, "Usuario", zona);

            ActualizarEstadisticas();
        }

        private void PublishCommand(string topic, string message)
        {
            if (mqttClient != null && mqttClient.IsConnected)
            {
                mqttClient.Publish(topic, System.Text.Encoding.UTF8.GetBytes(message),
                                  MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            }
        }

        #endregion

        #region Automatización por Sensor de Movimiento

        private void ProcesarMovimiento(string zona, bool presencia)
        {
            if (!timersApagado.ContainsKey(zona)) return;

            estadoMovimiento[zona] = presencia;

            if (presencia)
            {
                // Detener temporizador si está activo
                timersApagado[zona].Stop();

                // Encender luz automáticamente
                PublishCommand($"{BASE_TOPIC}/{zona}/luz/comando", "ON");
                AgregarEvento($"Luz {zona}", "Encendido por sensor", "Automatización");
                GuardarEvento($"Luz_{zona}", "Encendido", "Automatico_Sensor", zona);

                // Actualizar UI del sensor
                ActualizarUIMovimiento(zona, true);

                // Guardar medición del sensor
                GuardarMedicion(zona, "movimiento", 1);
            }
            else
            {
                // Iniciar temporizador de apagado
                timersApagado[zona].Start();
                AgregarEvento($"Sensor {zona}", "Sin movimiento - temporizador iniciado", "Automatización");

                // Actualizar UI del sensor
                ActualizarUIMovimiento(zona, false);

                // Guardar medición del sensor
                GuardarMedicion(zona, "movimiento", 0);
            }
        }

        private void Timer_Apagado_Tick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            string zona = timer.Tag.ToString();

            // Verificar si sigue sin movimiento
            if (!estadoMovimiento[zona])
            {
                // Apagar luz automáticamente
                PublishCommand($"{BASE_TOPIC}/{zona}/luz/comando", "OFF");
                AgregarEvento($"Luz {zona}", "Apagado por inactividad (5 min)", "Automatización");
                GuardarEvento($"Luz_{zona}", "Apagado", "Automatico_Temporizador", zona);

                timer.Stop();
            }
        }

        private void ActualizarUIMovimiento(string zona, bool presencia)
        {
            string texto = presencia ? "Sensor: ON" : "Sensor: OFF";
            SolidColorBrush color = presencia ? new SolidColorBrush(Colors.Green) : new SolidColorBrush(Colors.Gray);

            switch (zona)
            {
                case "sala":
                    txtMovSala.Text = texto;
                    txtMovSala.Foreground = color;
                    break;
                case "porche":
                    // Agregar TextBlock txtMovPorche en XAML si es necesario
                    break;
            }
        }

        #endregion

        #region Actualización de UI

        private void ActualizarTemperatura(string zona, double temperatura)
        {
            string tempText = $"{temperatura:F1}°C";

            switch (zona)
            {
                case "porche":
                    txtTempPorche.Text = tempText;
                    break;
                case "sala":
                    txtTempSala.Text = tempText;
                    break;
                case "cocina":
                    txtTempCocina.Text = tempText;
                    break;
                case "habitacion1":
                    txtTempHab1.Text = tempText;
                    break;
                case "habitacion2":
                    txtTempHab2.Text = tempText;
                    break;
            }
        }

        private void ActualizarEstadoLuz(string zona, bool encendido)
        {
            ToggleButton toggle = null;

            switch (zona)
            {
                case "porche": toggle = toggleLuzPorche; break;
                case "sala": toggle = toggleLuzSala; break;
                case "cocina": toggle = toggleLuzCocina; break;
                case "habitacion1": toggle = toggleLuzHab1; break;
                case "habitacion2": toggle = toggleLuzHab2; break;
                case "bano": toggle = toggleLuzBano; break;
                case "patio": toggle = toggleLuzPatio; break;
            }

            if (toggle != null)
            {
                toggle.IsChecked = encendido;
                toggle.Background = encendido ? new SolidColorBrush(Color.FromRgb(241, 196, 15)) :
                                                new SolidColorBrush(Colors.LightGray);
            }

            ActualizarEstadisticas();
        }

        private void ActualizarEstadoAA(string zona, bool encendido)
        {
            ToggleButton toggle = null;

            switch (zona)
            {
                case "porche": toggle = toggleAAPorche; break;
                case "sala": toggle = toggleAASala; break;
                case "habitacion1": toggle = toggleAAHab1; break;
                case "habitacion2": toggle = toggleAAHab2; break;
            }

            if (toggle != null)
            {
                toggle.IsChecked = encendido;
                toggle.Background = encendido ? new SolidColorBrush(Color.FromRgb(52, 152, 219)) :
                                                new SolidColorBrush(Colors.LightGray);
            }

            ActualizarEstadisticas();
        }

        private void ActualizarEstadisticas()
        {
            int lucesEncendidas = 0;
            int aasEncendidos = 0;

            // Contar luces encendidas
            if (toggleLuzPorche.IsChecked == true) lucesEncendidas++;
            if (toggleLuzSala.IsChecked == true) lucesEncendidas++;
            if (toggleLuzCocina.IsChecked == true) lucesEncendidas++;
            if (toggleLuzHab1.IsChecked == true) lucesEncendidas++;
            if (toggleLuzHab2.IsChecked == true) lucesEncendidas++;
            if (toggleLuzBano.IsChecked == true) lucesEncendidas++;
            if (toggleLuzPatio.IsChecked == true) lucesEncendidas++;

            // Contar AAs encendidos
            if (toggleAAPorche.IsChecked == true) aasEncendidos++;
            if (toggleAASala.IsChecked == true) aasEncendidos++;
            if (toggleAAHab1.IsChecked == true) aasEncendidos++;
            if (toggleAAHab2.IsChecked == true) aasEncendidos++;

            txtStats.Text = $"Luces encendidas: {lucesEncendidas}\nAA encendidos: {aasEncendidos}\nEventos: {eventos.Count}";
        }

        #endregion

        #region Eventos del Mouse

        private void Grid_MouseMove(object sender, MouseEventArgs e)
        {
            Point position = e.GetPosition(gridCasa);
            txtMousePos.Text = $"X: {position.X:F0}, Y: {position.Y:F0}";
        }

        private void Room_MouseEnter(object sender, MouseEventArgs e)
        {
            Border room = sender as Border;
            if (room != null)
            {
                string zona = room.Tag.ToString();
                txtHoverRoom.Text = $"Zona: {zona.ToUpper()}";
                room.Background = new SolidColorBrush(Color.FromRgb(189, 195, 199));
            }
        }

        private void Room_MouseLeave(object sender, MouseEventArgs e)
        {
            Border room = sender as Border;
            if (room != null)
            {
                txtHoverRoom.Text = "Zona: Ninguna";

                // Restaurar color original según la zona
                if (room.Tag.ToString() == "patio")
                    room.Background = new SolidColorBrush(Color.FromRgb(168, 230, 207));
                else
                    room.Background = new SolidColorBrush(Color.FromRgb(236, 240, 241));
            }
        }

        #endregion

        #region Utilidades

        private void AgregarEvento(string equipo, string mensaje, string origen)
        {
            eventos.Insert(0, new EventoLog
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Equipo = equipo,
                Mensaje = $"[{equipo}] {mensaje} ({origen})",
                Origen = origen
            });

            // Limitar a 100 eventos en la UI
            if (eventos.Count > 100)
            {
                eventos.RemoveAt(eventos.Count - 1);
            }
        }

        #endregion
    }

    // Clase para el log de eventos
    public class EventoLog
    {
        public string Timestamp { get; set; }
        public string Equipo { get; set; }
        public string Mensaje { get; set; }
        public string Origen { get; set; }
    }
}
