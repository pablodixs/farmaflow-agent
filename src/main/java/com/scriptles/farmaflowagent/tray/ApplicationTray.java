package com.scriptles.farmaflowagent.tray;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.DisposableBean;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.context.event.ApplicationReadyEvent;
import org.springframework.context.ApplicationListener;
import org.springframework.context.ConfigurableApplicationContext;
import org.springframework.stereotype.Component;

import java.awt.AWTException;
import java.awt.Color;
import java.awt.Desktop;
import java.awt.EventQueue;
import java.awt.Font;
import java.awt.Graphics2D;
import java.awt.GraphicsEnvironment;
import java.awt.Image;
import java.awt.MenuItem;
import java.awt.PopupMenu;
import java.awt.SystemTray;
import java.awt.TrayIcon;
import java.awt.image.BufferedImage;
import java.io.InputStream;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;

import javax.imageio.ImageIO;

@Component
public class ApplicationTray implements ApplicationListener<ApplicationReadyEvent>, DisposableBean {

    private static final Logger LOGGER = LoggerFactory.getLogger(ApplicationTray.class);

    private final ConfigurableApplicationContext context;
    private final int serverPort;
    private final String trayIconPath;

    private TrayIcon trayIcon;

    public ApplicationTray(
            ConfigurableApplicationContext context,
            @Value("${server.port:3333}") int serverPort,
            @Value("${agent.tray.icon-path:}") String trayIconPath
    ) {
        this.context = context;
        this.serverPort = serverPort;
        this.trayIconPath = trayIconPath;
    }

    @Override
    public void onApplicationEvent(ApplicationReadyEvent event) {
        if (GraphicsEnvironment.isHeadless() || !SystemTray.isSupported()) {
            LOGGER.info("Tray de aplicativos indisponivel neste ambiente.");
            return;
        }

        EventQueue.invokeLater(this::installTrayIcon);
    }

    @Override
    public void destroy() {
        if (trayIcon != null && SystemTray.isSupported()) {
            SystemTray.getSystemTray().remove(trayIcon);
            trayIcon = null;
        }
    }

    private void installTrayIcon() {
        if (trayIcon != null) {
            return;
        }

        PopupMenu menu = new PopupMenu();
        MenuItem statusItem = new MenuItem("FarmaFlow Agent");
        statusItem.setEnabled(false);

        MenuItem openApp = new MenuItem("Abrir FarmaFlow");
        openApp.addActionListener(event -> openWebApp());

        MenuItem openItem = new MenuItem("Abrir painel");
        openItem.addActionListener(event -> openLocalPanel());

        MenuItem statusItemLink = new MenuItem("Status do dispositivo");
        statusItemLink.addActionListener(event -> openLocalUrl("/status.html"));

        MenuItem updateItem = new MenuItem("Verificar atualizacao");
        updateItem.addActionListener(event -> openLocalUrl("/update.html"));

        MenuItem exitItem = new MenuItem("Sair");
        exitItem.addActionListener(event -> EventQueue.invokeLater(context::close));

        menu.add(statusItem);
        menu.addSeparator();
        menu.add(openApp);
        menu.add(openItem);
        menu.add(statusItemLink);
        menu.add(updateItem);
        menu.add(exitItem);

        trayIcon = new TrayIcon(createTrayImage(), "FarmaFlow Agent", menu);
        trayIcon.setImageAutoSize(true);
        trayIcon.addActionListener(event -> openLocalPanel());

        try {
            SystemTray.getSystemTray().add(trayIcon);
            trayIcon.displayMessage(
                    "FarmaFlow Agent",
                    "Rodando em segundo plano.",
                    TrayIcon.MessageType.NONE
            );
        } catch (AWTException exception) {
            LOGGER.warn("Nao foi possivel adicionar o tray de aplicativos.", exception);
        }
    }

    private void openLocalPanel() {
        openLocalUrl("/index.html");
    }

    private void openLocalUrl(String path) {
        if (!Desktop.isDesktopSupported()) {
            return;
        }

        try {
            Desktop.getDesktop().browse(URI.create("http://localhost:" + serverPort + path));
        } catch (Exception exception) {
            LOGGER.warn("Nao foi possivel abrir o painel local.", exception);
        }
    }

    private void openWebApp() {
        if (!Desktop.isDesktopSupported()) {
            return;
        }

        try {
            Desktop.getDesktop().browse(URI.create("https://farmaflow-rho.vercel.app/dashboard"));
        } catch (Exception exception) {
            LOGGER.warn("Nao foi possivel abrir o painel local.", exception);
        }
    }

    private Image createTrayImage() {
        Image configuredImage = loadConfiguredTrayImage();

        if (configuredImage != null) {
            return configuredImage;
        }

        int size = 16;
        BufferedImage image = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
        Graphics2D graphics = image.createGraphics();

        graphics.setColor(new Color(22, 163, 74));
        graphics.fillRoundRect(0, 0, size, size, 4, 4);
        graphics.setColor(Color.WHITE);
        graphics.setFont(new Font(Font.SANS_SERIF, Font.BOLD, 11));
        graphics.drawString("F", 4, 12);
        graphics.dispose();

        return image;
    }

    private Image loadConfiguredTrayImage() {
        if (trayIconPath != null && !trayIconPath.isBlank()) {
            try {
                return ImageIO.read(Path.of(trayIconPath).toFile());
            } catch (Exception exception) {
                LOGGER.warn("Nao foi possivel carregar o icone configurado: {}", trayIconPath, exception);
            }
        }

        try (InputStream inputStream = getClass().getResourceAsStream("/tray-icon.png")) {
            if (inputStream != null) {
                return ImageIO.read(inputStream);
            }
        } catch (Exception exception) {
            LOGGER.warn("Nao foi possivel carregar /tray-icon.png.", exception);
        }

        try {
            Path defaultPath = Path.of("tray-icon.png");
            if (Files.exists(defaultPath)) {
                return ImageIO.read(defaultPath.toFile());
            }
        } catch (Exception exception) {
            LOGGER.warn("Nao foi possivel carregar tray-icon.png do diretorio atual.", exception);
        }

        return null;
    }
}
